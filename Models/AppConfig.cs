namespace ComfeeRemote.Models;

public sealed class AppConfig
{
    public string Name { get; set; } = "Klimaanlage";
    public string IpAddress { get; set; } = "";
    public int Port { get; set; } = 6444;
    public long DeviceId { get; set; } = 0;
    public int DeviceType { get; set; } = 172;
    public int DeviceProtocol { get; set; } = 2;
    public string Model { get; set; } = "";

    // Wird beim Start automatisch über QueryAppliance ermittelt.
    public int MessageProtocol { get; set; } = 0;

    public int RefreshSeconds { get; set; } = 8;
}