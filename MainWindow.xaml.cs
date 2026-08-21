using System.ComponentModel;
using System.Drawing;
using System.IO;
using Forms = System.Windows.Forms;
using System.Windows;
using System.Windows.Threading;
using ComfeeRemote.Models;
using ComfeeRemote.Services;

namespace ComfeeRemote;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly MideaV2Protocol _ac;
    private readonly DispatcherTimer _refreshTimer;

    private AcState _state = new();
    private bool _busy;
    private bool _closing;

    private readonly Forms.NotifyIcon _trayIcon;
    private bool _allowClose;

    // SHORT CUT wird lokal gespeichert, bis wir die genaue Hardware-Funktion
    // deines Modells festlegen.
    private AcState? _shortcut;

    public MainWindow()
    {
        InitializeComponent();

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Comfee Remote",
            Visible = false
        };

        try
        {
            string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "comfee.ico");
            _trayIcon.Icon = new System.Drawing.Icon(iconPath);
            if (File.Exists(iconPath))
                _trayIcon.Icon = new Icon(iconPath);
            else
                _trayIcon.Icon = SystemIcons.Application;
        }
        catch
        {
            _trayIcon.Icon = SystemIcons.Application;
        }

        var trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("Öffnen", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add("Beenden", null, (_, _) => ExitApplication());
        _trayIcon.ContextMenuStrip = trayMenu;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        
        

        StateChanged += MainWindow_StateChanged;

        _config = ConfigService.Load();
        _ac = new MideaV2Protocol(_config);

        ConnectionText.Text = $"{_config.IpAddress}:{_config.Port} · {_config.Model}";
        KlimaName.Text = $"COMFEE - {_config.Name}";

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(3, _config.RefreshSeconds))
        };
        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync(false);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Status("Initialisiere Midea V2...");
            await _ac.InitializeAsync();
            await RefreshStatusAsync(true);
            _refreshTimer.Start();
        }
        catch (Exception ex)
        {
            Status($"Verbindung fehlgeschlagen: {ex.Message}");
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }

        _closing = true;
        _refreshTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            MinimizeToTray();
    }

    private void MinimizeToTray()
    {
        _trayIcon.Visible = true;
        ShowInTaskbar = false;
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        _trayIcon.Visible = false;
    }

    private void ExitApplication()
    {
        _allowClose = true;
        Close();
    }

    private async Task RefreshStatusAsync(bool showErrors)
    {
        if (_busy || _closing)
            return;

        _busy = true;
        try
        {
            var state = await _ac.ReadStatusAsync();

            // Bei diesem älteren V2-Modul kann eine Antwort direkt nach einem
            // Schreibbefehl noch den vorherigen Sollwert enthalten.
            // Der periodische Status holt anschließend den aktuellen Wert.
            _state = state;
            RenderState();

            Status($"Verbunden · Protokoll V2 / Message {_ac.MessageProtocol} · {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            if (showErrors)
                System.Windows.MessageBox.Show(this, ex.Message, "Comfee Remote",
                    MessageBoxButton.OK, MessageBoxImage.Warning);

            Status($"Keine Antwort: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task SendStateAsync(Action<AcState> change)
    {
        if (_busy)
            return;

        _busy = true;

        try
        {
            change(_state);

            // Sofort lokal aktualisieren, weil das Gerät den neuen Wert
            // physisch übernimmt, sein Statusframe aber kurz alt sein kann.
            RenderState();
            Status("Sende...");

            await _ac.SetStateAsync(_state);

            Status($"Gesendet · {DateTime.Now:HH:mm:ss}");

            // Kein sofortiges Überschreiben mit einem eventuell alten Frame.
            // Nach 2,5 s versuchen wir eine frische Abfrage.
            await Task.Delay(2500);

            if (!_closing)
            {
                var read = await _ac.ReadStatusAsync();

                // Wenn nur der Sollwert noch alt zurückkommt, behalten wir den
                // gerade gesendeten Sollwert; alle anderen aktuellen Werte nehmen wir.
                if (Math.Abs(read.TargetTemperature - _state.TargetTemperature) > 0.01)
                    read.TargetTemperature = _state.TargetTemperature;

                _state = read;
                RenderState();
            }
        }
        catch (Exception ex)
        {
            Status($"Fehler: {ex.Message}");
            System.Windows.MessageBox.Show(this, ex.Message, "Comfee Remote",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
        }
    }

    private void RenderState()
    {
        TargetTempText.Text = _state.TargetTemperature.ToString("0.#");
        PowerText.Text = _state.Power ? "● EIN" : "○ AUS";
        ModeText.Text = ModeName(_state.Mode);
        FanText.Text = $"FAN {FanName(_state.FanSpeed)}";

        // var indoor = _state.IndoorTemperature?.ToString("0.#") ?? "--";
        var indoor = _state.IndoorTemperature.HasValue
                        ? (_state.IndoorTemperature.Value + 4).ToString("0.#")
                        : "--";
        var outdoor = _state.OutdoorTemperature?.ToString("0.#") ?? "--";
        RoomTempText.Text = $"Innen {indoor} °C  |  Außen {outdoor} °C";

        SwingStatusText.Text =
            _state.SwingVertical ? "SWING ●" : "SWING ○";

        TurboStatusText.Text =
            _state.Turbo ? "TURBO ●" : "TURBO ○";
    }

    private void Status(string text)
    {
        StatusText.Text = text;
    }

    private static string ModeName(int mode) => mode switch
    {
        1 => "AUTO",
        2 => "COOL",
        3 => "DRY",
        4 => "HEAT",
        5 => "FAN",
        _ => $"MODE {mode}"
    };

    private static string FanName(int fan) => fan switch
    {
        102 => "AUTO",
        <= 40 => "LOW",
        <= 60 => "MED",
        <= 80 => "HIGH",
        _ => fan.ToString()
    };

    private async void Power_Click(object sender, RoutedEventArgs e) =>
        await SendStateAsync(s => s.Power = !s.Power);

    private async void TempUp_Click(object sender, RoutedEventArgs e) =>
        await SendStateAsync(s => s.TargetTemperature = Math.Min(30, s.TargetTemperature + 1));

    private async void TempDown_Click(object sender, RoutedEventArgs e) =>
        await SendStateAsync(s => s.TargetTemperature = Math.Max(16, s.TargetTemperature - 1));

    private async void Mode_Click(object sender, RoutedEventArgs e) =>
        await SendStateAsync(s =>
        {
            // AUTO -> COOL -> DRY -> HEAT -> FAN
            s.Mode = s.Mode switch
            {
                1 => 2,
                2 => 3,
                3 => 4,
                4 => 5,
                _ => 1
            };
        });

    private async void Fan_Click(object sender, RoutedEventArgs e) =>
        await SendStateAsync(s =>
        {
            // AUTO -> LOW -> MED -> HIGH -> AUTO
            s.FanSpeed = s.FanSpeed switch
            {
                102 => 40,
                <= 40 => 60,
                <= 60 => 80,
                _ => 102
            };
        });

    private async void Sleep_Click(object sender, RoutedEventArgs e) =>
        await SendStateAsync(s => s.Sleep = !s.Sleep);

    private async void Turbo_Click(object sender, RoutedEventArgs e) =>
        await SendStateAsync(s => s.Turbo = !s.Turbo);

    private async void Swing_Click(object sender, RoutedEventArgs e) =>
        await SendStateAsync(s => s.SwingVertical = !s.SwingVertical);

    private void Direct_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(this,
            "DIRECT ist optisch schon vorhanden. Für die exakten Lamellen-Winkel deines Modells bauen wir den Befehl als Nächstes ein.",
            "DIRECT", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Led_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        _busy = true;
        try
        {
            Status("LED-Befehl...");
            await _ac.ToggleLedAsync();
            _state.ScreenDisplay = !_state.ScreenDisplay;
            Status("LED umgeschaltet");
        }
        catch (Exception ex)
        {
            Status($"LED Fehler: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private async void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_shortcut is null)
        {
            _shortcut = _state.Clone();
            Status($"SHORT CUT gespeichert: {ModeName(_state.Mode)} {_state.TargetTemperature:0.#} °C");
            return;
        }

        var saved = _shortcut.Clone();
        await SendStateAsync(s =>
        {
            s.Power = saved.Power;
            s.Mode = saved.Mode;
            s.TargetTemperature = saved.TargetTemperature;
            s.FanSpeed = saved.FanSpeed;
            s.SwingVertical = saved.SwingVertical;
            s.SwingHorizontal = saved.SwingHorizontal;
            s.Turbo = saved.Turbo;
            s.Sleep = saved.Sleep;
        });
    }

    private void TimerOn_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(this,
            "TIMER ON ist in der Oberfläche vorbereitet. Den Zeitdialog + Timer-Protokoll können wir im nächsten Schritt ergänzen.",
            "TIMER ON", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void TimerOff_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(this,
            "TIMER OFF ist in der Oberfläche vorbereitet. Den Zeitdialog + Timer-Protokoll können wir im nächsten Schritt ergänzen.",
            "TIMER OFF", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshStatusAsync(true);
}
