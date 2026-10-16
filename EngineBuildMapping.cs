namespace ForzavistaFreeRoam;

// Exact code and RTTI mappings are independently verified for each platform.
// Selection also checks PE identity; a matching display name alone is insufficient.
internal sealed record EngineBuildMapping(string Name, uint PeTimestamp, uint SizeOfImage,
    ulong ServiceGlobalRva, ulong ServiceVtableRva, ulong PlayerVtableRva, ulong VehicleVtableRva,
    ulong SetterRva, ulong TorqueSetterRva, ulong NativeBodyRva, ulong PhysicsVtableRva,
    ulong RpmRva, ulong PhysicsTorqueRva, byte[] SetterSignature, byte[] NativeBodySignature,
    byte[] RpmSignature, byte[] TorqueSignature, byte[] PhysicsTorqueSignature)
{
    internal static EngineBuildMapping Steam461 { get; } = new(
        "Steam 6.461.691.0", 0x6ABA8552, 0xB3C0000,
        0xA8109C8, 0x64C5320, 0x64C4B08, 0x64A3D38, 0x8F72F0,
        0x8F7340, 0x2AC67A0, 0x6C4B5F0, 0x31C3870, 0x31D6770,
        Convert.FromHexString("48895C24084889742410574883EC200FB699789200000FB6FA488BF1E88FF41C02403ADF7411488B8EC89700004885C97405E889773400488B5C2430488B7424384883C4205FC3"),
        Convert.FromHexString("48896C24104889742418574883EC200FB6F2488BF984D2743B8BA9E8780000B20148895C24300FB699B0770000488B8960910000E8B7BBF1FF84DB488B5C2430751283FD01740983BFE878000003750484C0753C80BF789200000074254084F67520488B8F40770000488B01FF9058030000F30F5E05BAC51804F30F11877C9200004088B7789200004088B74E920000488B6C2438488B7424404883C4205FC3"),
        Convert.FromHexString("F30F10814C0200000F57C9F30F5D81B0010000F30F5FC1F30F5905416E3A03C3"),
        Convert.FromHexString("F30F1189B0940000C3"),
        Convert.FromHexString("F30F5D0DD0012603F30F118920020000C3"));

    internal static EngineBuildMapping Xbox461 { get; } = new(
        "Microsoft Store 3.461.691.0", 0x6ABA856E, 0xB3AD000,
        0xA80A248, 0x64B21B0, 0x64B1998, 0x6490C18, 0x87E490,
        0x87E4E0, 0x2A48230, 0x6C382F0, 0x3143570, 0x3156470,
        Convert.FromHexString("48895C24084889742410574883EC200FB699789200000FB6FA488BF1E87F9D1C02403ADF7411488B8EC89700004885C97405E899783400488B5C2430488B7424384883C4205FC3"),
        Convert.FromHexString("48896C24104889742418574883EC200FB6F2488BF984D2743B8BA9E8780000B20148895C24300FB699B0770000488B8960910000E837BAF1FF84DB488B5C2430751283FD01740983BFE878000003750484C0753C80BF789200000074254084F67520488B8F40770000488B01FF9058030000F30F5E052A781F04F30F11877C9200004088B7789200004088B74E920000488B6C2438488B7424404883C4205FC3"),
        Convert.FromHexString("F30F10814C0200000F57C9F30F5D81B0010000F30F5FC1F30F590581434103C3"),
        Convert.FromHexString("F30F1189B0940000C3"),
        Convert.FromHexString("F30F5D0DD0D32C03F30F118920020000C3"));

    internal static EngineBuildMapping? ForProfile(GameBuildProfile profile)
    {
        var map = ForName(profile.Name);
        return map is not null && profile.PeTimestamp == map.PeTimestamp &&
            profile.SizeOfImage == map.SizeOfImage ? map : null;
    }

    internal static EngineBuildMapping? ForName(string name) => name switch
    {
        "Steam 6.461.691.0" => Steam461,
        "Microsoft Store 3.461.691.0" => Xbox461,
        _ => null
    };
}
