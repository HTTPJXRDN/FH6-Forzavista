namespace ForzavistaFreeRoam;

internal sealed record EngineCallBinding(int ProcessId, ulong Module, ulong Vehicle,
    ulong Component, ulong Service, ulong Player, ulong Physics, string? CarToken,
    string BuildName = "Steam 6.461.691.0");

internal sealed record EnginePowerOwnership(EngineCallBinding Binding, bool Original,
    float OriginalTorque, float ExpectedTorque)
{
    private static bool Same(float left, float right) =>
        BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);
    internal bool OwnsTorque(float current) => Same(current, ExpectedTorque);
    internal float RequestedTorque(bool on) => on ? OriginalTorque : 0f;
    internal float CleanupTorque(float current) => OwnsTorque(current) ? OriginalTorque : current;
    internal bool IsOriginal(bool on, float torque) => on == Original && Same(torque, OriginalTorque);
}

// The guard executes in the game immediately before its native ignition setter.
// The binding selects an exact, independently verified platform mapping.
internal static class EngineIgnitionCall
{

    internal static bool SupportsCarState(uint carMode, byte modeFlag, byte presentation) =>
        carMode == 0 && modeFlag <= 1 && presentation <= 1;

    internal static byte[] Build(EngineCallBinding owner, bool engineOn, float torque,
        float expectedTorque, bool expectedOn, bool presentation, bool modeFlag = false)
    {
        if (!float.IsFinite(torque) || torque is < 0 or > 1 ||
            !float.IsFinite(expectedTorque) || expectedTorque is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(torque), "Unmapped native engine-power scalar.");
        var map = EngineBuildMapping.ForName(owner.BuildName) ??
            throw new InvalidOperationException("No native ignition mapping for the captured build.");
        var code = new List<byte>();
        var branches = new List<int>();
        void Emit(string hex) => code.AddRange(Convert.FromHexString(hex));
        void Imm64(string opcode, ulong value) { Emit(opcode); code.AddRange(BitConverter.GetBytes(value)); }
        void RejectIfDifferent() { Emit("0F85"); branches.Add(code.Count); code.AddRange(new byte[4]); }
        void CompareRax(ulong value) { Imm64("48BA", value); Emit("483BC2"); RejectIfDifferent(); }

        Imm64("49BA", owner.Module + map.ServiceGlobalRva);
        Emit("498B02");
        CompareRax(owner.Service);
        Emit("4C8BD0 498B02".Replace(" ", ""));
        CompareRax(owner.Module + map.ServiceVtableRva);
        Emit("41807A7001"); RejectIfDifferent();
        Emit("4983BA8000000001"); RejectIfDifferent(); // +0x80 needs a 32-bit displacement.
        Emit("4D8B52784D8B12498B4210");
        CompareRax(owner.Player);
        Emit("4C8BD0498B02");
        CompareRax(owner.Module + map.PlayerVtableRva);
        Emit("498B8270010000");
        CompareRax(owner.Vehicle);
        Emit("488BC8488B01");
        CompareRax(owner.Module + map.VehicleVtableRva);
        Emit("488B8120790000");
        CompareRax(owner.Component);
        Emit("488B8140770000");
        CompareRax(owner.Physics);
        Emit("488B01488B8068020000");
        CompareRax(owner.Module + map.SetterRva);
        Emit("488B8140770000488B00");
        CompareRax(owner.Module + map.PhysicsVtableRva);
        Emit("83B9E878000000"); RejectIfDifferent();
        // Both boolean values reach the native setter for car mode zero.
        // Guard the captured value rather than assuming the flag stays zero.
        Emit("80B9B0770000"); Emit(modeFlag ? "01" : "00"); RejectIfDifferent();
        Emit("80B9A3820000"); Emit(presentation ? "01" : "00"); RejectIfDifferent();
        Emit("81B9B0940000"); code.AddRange(BitConverter.GetBytes(expectedTorque)); RejectIfDifferent();
        foreach (var field in new[] { 0x9278, 0x924E })
        {
            Emit("80B9"); code.AddRange(BitConverter.GetBytes(field));
            Emit(expectedOn ? "01" : "00"); RejectIfDifferent();
        }
        // EventLab's native engine-power setter takes its second float argument
        // in XMM1. Preserve the vehicle across both calls; keep shadow space and
        // stack alignment. No physics arrays or torque curves are edited.
        Emit("534883EC20488BD9B8"); code.AddRange(BitConverter.GetBytes(torque));
        Emit("660F6EC8");
        Imm64("48B8", owner.Module + map.TorqueSetterRva);
        Emit("FFD0488BCB");
        Emit("BA"); code.AddRange(BitConverter.GetBytes(engineOn ? 1 : 0));
        Imm64("48B8", owner.Module + map.SetterRva);
        Emit("FFD04883C4205B33C0C3");
        var failure = code.Count;
        Emit("B801E00000C3");
        foreach (var at in branches)
        {
            var displacement = BitConverter.GetBytes(failure - (at + 4));
            for (var i = 0; i < 4; i++) code[at + i] = displacement[i];
        }
        return code.ToArray();
    }
}
