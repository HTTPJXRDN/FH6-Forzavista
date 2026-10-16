using Microsoft.Win32.SafeHandles;

namespace ForzavistaFreeRoam;

internal static partial class NativeCarControl
{
    private static readonly MaxDetailController MaxDetail = new(OpenMaxDetailRenderSession);
    private static readonly object RenderObservationGate = new();
    private static (RenderModeIdentity Identity, RenderModeState State)? _lastRenderObservation;
    private static (RenderModeIdentity Identity, MaxDetailSceneState Scene)? _lastSceneObservation;

    internal static NativeMaxDetailStatus GetMaxDetailStatus()
    {
        try { return MaxDetail.GetStatus(); }
        catch (Exception error)
        {
            return MaxDetail.GetUnavailableStatus("MAX DETAIL status unavailable: " + error.GetBaseException().Message);
        }
    }

    internal static MaxDetailResult SetMaxDetail(bool on)
    {
        var result = MaxDetail.SetEnabled(on);
        SessionLog.Write("max_detail_result", $"requestedOn={on}; success={result.Success}; owned={result.On}; {result.Message}");
        return result;
    }

    internal static NativeLightActionResult RestoreTrackedMaxDetail()
    {
        try
        {
            var result = MaxDetail.Restore();
            SessionLog.Write("max_detail_cleanup", $"success={result.Success}; owned={result.On}; {result.Message}");
            return new(result.Success, result.Message);
        }
        catch (Exception error) { return new(false, "MAX DETAIL cleanup not verified: " + error.GetBaseException().Message); }
    }

    private static IMaxDetailRenderSession? OpenMaxDetailRenderSession(bool writable)
    {
        using var game = TryGetGame();
        if (game is null) return null;
        var (handle, module, profile, processId, startTicks) = OpenGameModule(action: writable);
        try
        {
            var controller = ResolveRenderController(handle, module, profile);
            return new MaxDetailRenderSession(handle, profile,
                new(processId, startTicks, module, controller), writable);
        }
        catch { handle.Dispose(); throw; }
    }

    private sealed class MaxDetailRenderSession(SafeProcessHandle handle, GameBuildProfile profile,
        RenderModeIdentity identity, bool writable) : IMaxDetailRenderSession
    {
        public RenderModeIdentity Identity => identity;
        public MaxDetailModeMap Modes => profile.MaxDetailModes ?? MaxDetailModeMap.Legacy;

        public RenderModeState ReadState()
        {
            if (ResolveRenderController(handle, identity.Module, profile) != identity.Controller)
                throw new InvalidOperationException("Render controller changed during the operation.");
            var bytes = Read(handle, identity.Controller + 0x2B0, 16);
            if (bytes[8] > 1) throw new InvalidOperationException("Render transition flag is invalid.");
            var state = new RenderModeState(BitConverter.ToInt32(bytes, 0), BitConverter.ToInt32(bytes, 4),
                BitConverter.ToInt32(bytes, 12), bytes[8]);
            lock (RenderObservationGate)
            {
                if (_lastRenderObservation != (identity, state))
                {
                    _lastRenderObservation = (identity, state);
                    SessionLog.Write("render_mode_observed",
                        $"controller=0x{identity.Controller:X}; current={state.Current}; previous={state.Previous}; option={state.Option}; pending={state.Pending}",
                        gamePid: identity.ProcessId);
                }
            }
            return state;
        }

        public MaxDetailSceneState? ReadScene()
        {
            if (profile.MaxDetailScenes is not { } map) return null;
            var root = ReadUInt64(handle, identity.Module + map.RootGlobalRva);
            if (root == 0) return new(0, 0, 0, 0, MaxDetailSceneKind.Other);
            ValidateScenePointer(root);
            if (ReadUInt64(handle, root) != identity.Module + map.RootVtableRva)
                throw new InvalidOperationException("MAX DETAIL UI root signature changed.");
            var table = ReadUInt64(handle, root + 0xA0);
            if (table == 0) return new(root, 0, 0, 0, MaxDetailSceneKind.Other);
            ValidateScenePointer(table);
            var scene = ReadUInt64(handle, table + 0xC8);
            if (scene == 0) return new(root, table, 0, 0, MaxDetailSceneKind.Other);
            ValidateScenePointer(scene);
            var vtable = ReadUInt64(handle, scene);
            var kind = vtable == identity.Module + map.PhotoVtableRva ? MaxDetailSceneKind.Photo :
                vtable == identity.Module + map.EmptyVtableRva ? MaxDetailSceneKind.Empty :
                (map.GarageVtableRva != 0 && vtable == identity.Module + map.GarageVtableRva ||
                 map.GarageMenuVtableRva != 0 && vtable == identity.Module + map.GarageMenuVtableRva) ?
                    MaxDetailSceneKind.Garage : MaxDetailSceneKind.Other;
            var saved = 0;
            var phase = 0;
            if (kind == MaxDetailSceneKind.Photo)
            {
                if (ReadUInt64(handle, scene + 0x80) != identity.Module + map.PhotoSecondaryVtableRva)
                    throw new InvalidOperationException("Photo Mode scene signature changed.");
                saved = ReadInt32(handle, scene + 0x1B2C);
                phase = ReadInt32(handle, scene + 0x1ADC);
            }
            // Check that this is still the active scene after reading its fields.
            if (ReadUInt64(handle, identity.Module + map.RootGlobalRva) != root ||
                ReadUInt64(handle, root + 0xA0) != table || ReadUInt64(handle, table + 0xC8) != scene ||
                ReadUInt64(handle, scene) != vtable)
                throw new InvalidOperationException("MAX DETAIL UI scene changed while reading.");
            var result = new MaxDetailSceneState(root, table, scene, vtable, kind, saved, phase);
            lock (RenderObservationGate)
            {
                if (_lastSceneObservation != (identity, result))
                {
                    _lastSceneObservation = (identity, result);
                    SessionLog.Write("max_detail_scene_observed",
                        $"scene=0x{scene:X}; kind={kind}; vtableRva=0x{vtable - identity.Module:X}; savedReturn={saved}; phase={phase}",
                        gamePid: identity.ProcessId);
                }
            }
            return result;
        }

        private static void ValidateScenePointer(ulong address)
        {
            if (address < 0x10000 || address >= 0x800000000000 || (address & 7) != 0)
                throw new InvalidOperationException("MAX DETAIL scene pointer is invalid.");
        }

        public void RepairPhotoReturn(MaxDetailSceneState expected, int mode)
        {
            if (!writable || profile.MaxDetailScenes is null || expected.Kind != MaxDetailSceneKind.Photo ||
                expected.Phase != 0 || expected.SavedReturnMode != Modes.Detail ||
                (mode != Modes.FreeRoam && mode != Modes.Garage) ||
                ReadState() is not { Pending: 0, Option: 0 } || ReadScene() != expected)
                throw new InvalidOperationException("Photo Mode scene or saved return value changed; no cache repair was published.");
            SessionLog.Write("max_detail_photo_return_request",
                $"scene=0x{expected.Scene:X}; {expected.SavedReturnMode} -> {mode}; captured normal origin",
                gamePid: identity.ProcessId);
            Write(handle, expected.Scene + 0x1B2C, BitConverter.GetBytes(mode));
            if (ReadScene() != expected with { SavedReturnMode = mode })
                throw new InvalidOperationException("Photo Mode saved return repair could not be verified.");
            SessionLog.Write("max_detail_photo_return_restored", $"scene=0x{expected.Scene:X}; savedReturn={mode}",
                gamePid: identity.ProcessId);
        }

        public void Publish(RenderModeState expected, int mode, int option, MaxDetailSceneState? expectedScene = null)
        {
            if (!writable || expected.Pending != 0 || ReadState() != expected ||
                (expectedScene is { } captured && (ReadScene() is not { } active ||
                 !captured.SameScene(active) || active.Kind == MaxDetailSceneKind.Photo && active.Phase != 0)))
                throw new InvalidOperationException("Render scene changed or a transition is pending; no new request was published.");
            SessionLog.Write("render_mode_request",
                $"build={profile.Name}; controller=0x{identity.Controller:X}; {expected.Current} -> {mode}; owned MAX DETAIL request",
                gamePid: identity.ProcessId);
            Write(handle, identity.Controller + 0x2BC, BitConverter.GetBytes(option));
            Write(handle, identity.Controller + 0x2B4, BitConverter.GetBytes(expected.Current));
            Write(handle, identity.Controller + 0x2B0, BitConverter.GetBytes(mode));
            if (ReadState() != new RenderModeState(mode, expected.Current, option, 0))
                throw new InvalidOperationException("Scene changed while staging the request; pending publication was refused.");
            Write(handle, identity.Controller + 0x2B8, [1]);
            SessionLog.Write("render_mode_published", $"{expected.Current} -> {mode}; waiting for native transition acknowledgement",
                gamePid: identity.ProcessId);
        }

        public void Dispose() => handle.Dispose();
    }
}
