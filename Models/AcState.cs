namespace ComfeeRemote.Models;

public sealed class AcState
{
    public bool Power { get; set; }
    public int Mode { get; set; } = 2;
    public double TargetTemperature { get; set; } = 20.0;
    public int FanSpeed { get; set; } = 102;

    public bool SwingVertical { get; set; }
    public bool SwingHorizontal { get; set; }
    public bool Turbo { get; set; }
    public bool PowerSaving { get; set; }
    public bool SmartEye { get; set; }
    public bool Dry { get; set; }
    public bool AuxHeating { get; set; }
    public bool Eco { get; set; }
    public bool Fahrenheit { get; set; }
    public bool Sleep { get; set; }
    public bool NaturalWind { get; set; }
    public bool FrostProtect { get; set; }
    public bool Comfort { get; set; }
    public bool Anion { get; set; }

    public bool ScreenDisplay { get; set; }
    public double? IndoorTemperature { get; set; }
    public double? OutdoorTemperature { get; set; }

    public AcState Clone() => (AcState)MemberwiseClone();
}
