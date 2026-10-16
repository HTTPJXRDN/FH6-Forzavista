using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ForzavistaFreeRoam;

internal sealed record NativeEngineStatus(bool Available, bool On, string Message);

internal static partial class NativeCarControl
{
    private static EnginePowerOwnership? _engineOwnership;
    private static int? _pendingEngineCallPid;

    private static NativeEngineStatus InspectEngine(NativeContext context, AnimationTarget target)
    {
        try
        {
            var (_, on, _) = CaptureEngine(context, target);
            return new(true, on, "OFF cuts engine sound and drive power; ON restores original drive power. RESET STATE and closing the tool restore the original state.");
        }
        catch (Exception ex) { return new(false, false, ex.Message); }
    }

    private static (EngineCallBinding Binding, bool On, float Torque) CaptureEngine(NativeContext context, AnimationTarget target)
    {
        void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
        var module = context.Module;
        ulong Q(ulong at) => ReadUInt64(context.Handle, at);
        byte B(ulong at) => Read(context.Handle, at, 1)[0];
        var map = EngineBuildMapping.ForProfile(context.Profile) ??
            throw new InvalidOperationException("Ignition is not mapped for this game build.");
        Require(_pendingEngineCallPid != context.ProcessId,
            "A previous ignition call did not finish; ignition cleanup cannot safely run concurrently.");
        Require(Read(context.Handle, module + map.SetterRva, map.SetterSignature.Length)
            .SequenceEqual(map.SetterSignature) &&
            Read(context.Handle, module + map.NativeBodyRva, map.NativeBodySignature.Length)
            .SequenceEqual(map.NativeBodySignature) &&
            Read(context.Handle, module + map.TorqueSetterRva, map.TorqueSignature.Length)
                .SequenceEqual(map.TorqueSignature) &&
            Read(context.Handle, module + map.PhysicsTorqueRva, map.PhysicsTorqueSignature.Length).SequenceEqual(map.PhysicsTorqueSignature),
            "Native ignition/power code changed; no engine action performed.");
        var service = Q(module + map.ServiceGlobalRva);
        Require(service >= 0x10000 && Q(service) == module + map.ServiceVtableRva,
            "Ignition player service changed.");
        Require(B(service + 0x70) == 1 && Q(service + 0x80) == 1,
            "Ignition requires the single local player.");
        var head = Q(service + 0x78);
        var node = Q(head);
        Require(node >= 0x10000 && node != head, "Ignition player list is empty.");
        var player = Q(node + 0x10);
        Require(player >= 0x10000 && Q(player) == module + map.PlayerVtableRva &&
            Q(player + 0x170) == target.Vehicle, "Ignition target is not the current player car.");
        Require(Q(target.Vehicle) == module + map.VehicleVtableRva &&
            Q(module + map.VehicleVtableRva + 0x268) == module + map.SetterRva,
            "Ignition vehicle type or native function changed.");
        Require(EngineIgnitionCall.SupportsCarState(
            BitConverter.ToUInt32(Read(context.Handle, target.Vehicle + 0x78E8, 4)),
            B(target.Vehicle + 0x77B0), B(target.Vehicle + 0x82A3)),
            "Ignition requires normal local car mode and valid state flags.");
        var physics = Q(target.Vehicle + 0x7740);
        Require(physics >= 0x10000 && Q(physics) == module + map.PhysicsVtableRva &&
            Q(module + map.PhysicsVtableRva + 0x358) == module + map.RpmRva &&
            Q(module + map.PhysicsVtableRva + 0x1668) == module + map.PhysicsTorqueRva &&
            Read(context.Handle, module + map.RpmRva, map.RpmSignature.Length).SequenceEqual(map.RpmSignature),
            "Ignition engine/RPM source is not mapped for this car.");
        var on = B(target.Vehicle + 0x9278);
        Require(on <= 1 && B(target.Vehicle + 0x924E) == on, "Native ignition state is inconsistent.");
        var torque = BitConverter.ToSingle(Read(context.Handle, target.Vehicle + 0x94B0, 4));
        Require(float.IsFinite(torque) && torque is >= 0 and <= 1, "Native engine-power scalar is outside the verified range.");
        ValidateUnchangedAnimationTarget(context, target);
        return (new(context.ProcessId, module, target.Vehicle, target.Component, service, player, physics,
            TryGetCurrentCarToken(context, target), map.Name), on == 1, torque);
    }

    internal static NativeEngineStatus ToggleEngine()
    {
        using var context = Locate(action: true);
        var target = ValidateAnimationTarget(context);
        var (binding, on, torque) = CaptureEngine(context, target);
        if (_engineOwnership?.Binding != binding) _engineOwnership = new(binding, on, torque, torque);
        if (!_engineOwnership.OwnsTorque(torque))
            throw new InvalidOperationException("Engine power changed outside the tool. Use RESET STATE before another ignition action.");
        var desiredTorque = _engineOwnership.RequestedTorque(!on);
        // Capture restoration ownership before dispatch: a later verification
        // failure must not lose an engine state that the native method changed.
        _engineOwnership = _engineOwnership with { ExpectedTorque = desiredTorque };
        InvokeEngine(context, target, binding, !on, desiredTorque, on, torque);
        var (_, after, afterTorque) = CaptureEngine(context, target);
        if (after != !on || afterTorque != desiredTorque)
            throw new InvalidOperationException("Native ignition/power did not reach the requested state; use RESET STATE to restore.");
        if (_engineOwnership.IsOriginal(after, afterTorque)) _engineOwnership = null;
        return new(true, after, after ? "Engine started; original drive power restored." : "Engine stopped; drive power OFF. Press IGNITION again to start.");
    }

    internal static NativeLightActionResult RestoreTrackedEngine()
    {
        if (_engineOwnership is not { } owned) return new(true, "no tracked ignition changes");
        try
        {
            using var game = TryGetGame();
            if (game is null || game.Id != owned.Binding.ProcessId)
            {
                _engineOwnership = null;
                return new(true, "previous engine session ended; no engine written");
            }
            using var context = Locate(action: true);
            var target = ValidateAnimationTarget(context);
            if (context.Module != owned.Binding.Module || target.Vehicle != owned.Binding.Vehicle ||
                target.Component != owned.Binding.Component)
            {
                _engineOwnership = null;
                return new(true, "current car changed; previous ignition ownership discarded");
            }
            var (binding, on, torque) = CaptureEngine(context, target);
            if (binding != owned.Binding)
            {
                _engineOwnership = null;
                return new(true, "engine owner changed; no engine written");
            }
            var restoreTorque = owned.CleanupTorque(torque);
            var externalPower = !owned.OwnsTorque(torque);
            if (on != owned.Original || torque != restoreTorque)
                InvokeEngine(context, target, binding, owned.Original, restoreTorque, on, torque);
            var (_, restored, restoredTorque) = CaptureEngine(context, target);
            if (restored != owned.Original || restoredTorque != restoreTorque)
                throw new InvalidOperationException("Original engine ignition/power state did not restore.");
            _engineOwnership = null;
            SessionLog.Write("ignition_restored", $"engineOn={restored}; torque={restoredTorque}; externalPowerPreserved={externalPower}", gamePid: binding.ProcessId,
                carToken: binding.CarToken, component: binding.Component);
            return new(true, externalPower ? "original ignition restored; outside engine-power setting preserved" : "original engine ignition and drive power restored");
        }
        catch (Exception ex) { return new(false, "Ignition cleanup failed: " + ex.Message); }
    }

    private static void InvokeEngine(NativeContext context, AnimationTarget target, EngineCallBinding owner,
        bool on, float torque, bool expectedOn, float expectedTorque)
    {
        var (current, currentOn, currentTorque) = CaptureEngine(context, target);
        if (current != owner) throw new InvalidOperationException("Ignition owner changed before dispatch.");
        if (currentOn != expectedOn || BitConverter.SingleToInt32Bits(currentTorque) != BitConverter.SingleToInt32Bits(expectedTorque))
            throw new InvalidOperationException("Ignition/power changed before dispatch; no engine action performed.");
        var remote = (ulong)VirtualAllocEx(context.Handle, 0, 0x1000, MemCommitReserve, 0x04);
        if (remote == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Ignition code allocation");
        using var allocation = new RemoteCallMemoryLease(
            () => VirtualFreeEx(context.Handle, (nuint)remote, 0, MemRelease),
            () => SessionLog.Write("remote_call_memory_retained", $"ignition; remote=0x{remote:X}; completion not verified", gamePid: context.ProcessId));
        var presentation = Read(context.Handle, target.Vehicle + 0x82A3, 1)[0] == 1;
        var modeFlag = Read(context.Handle, target.Vehicle + 0x77B0, 1)[0];
        if (modeFlag > 1) throw new InvalidOperationException("Ignition state flag changed before dispatch.");
        var code = EngineIgnitionCall.Build(owner, on, torque, currentTorque, currentOn, presentation, modeFlag == 1);
        Write(context.Handle, remote, code);
        if (!VirtualProtectEx(context.Handle, (nuint)remote, 0x1000, 0x20, out _) ||
            !FlushInstructionCache(context.Handle, (nuint)remote, (nuint)code.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Ignition code protection/cache flush");
        using var thread = CreateRemoteThread(context.Handle, 0, 0, (nuint)remote, 0, 0, out _);
        if (thread.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Ignition native thread");
        allocation.ThreadStarted();
        _pendingEngineCallPid = context.ProcessId;
        var wait = WaitForSingleObject(thread, 5000);
        SessionLog.Write("native_call_wait", $"ignition; engineOn={on}; torque={torque}; wait=0x{wait:X}", gamePid: owner.ProcessId,
            carToken: owner.CarToken, component: owner.Component);
        if (wait != 0) throw new InvalidOperationException("Native ignition call did not finish; its code remains allocated until game exit.");
        allocation.ThreadCompleted();
        _pendingEngineCallPid = null;
        if (!GetExitCodeThread(thread, out var exitCode)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Ignition native result");
        if (exitCode != 0) throw new InvalidOperationException($"Native ignition rejected the changed owner or failed (0x{exitCode:X}).");
        ValidateUnchangedAnimationTarget(context, target);
        SessionLog.Write("ignition_changed", $"engineOn={on}; torque={torque}", gamePid: owner.ProcessId,
            carToken: owner.CarToken, component: owner.Component);
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtectEx(SafeProcessHandle process,
        nuint address, nuint size, uint protection, out uint previousProtection);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeThread(SafeWaitHandle thread, out uint exitCode);
}
