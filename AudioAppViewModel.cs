using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace AppVolumeBoost;

public sealed class AudioAppViewModel : INotifyPropertyChanged
{
    private double _boostPercent;
    private string _stateLabel = "通常音量";

    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required string DisplayName { get; init; }
    public required NAudio.CoreAudioApi.AudioSessionControl Session { get; init; }
    public ImageSource? Icon { get; init; }

    public string ProcessLabel => $"{ProcessName}.exe  ·  PID {ProcessId}";

    public double BoostPercent
    {
        get => _boostPercent;
        set
        {
            var clamped = AudioBoostMath.ClampPercent(value);
            if (Math.Abs(_boostPercent - clamped) < 0.01) return;
            _boostPercent = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BoostDb));
            OnPropertyChanged(nameof(BoostLabel));
        }
    }

    public double BoostDb => AudioBoostMath.PercentToDb(BoostPercent);

    public string BoostLabel => BoostPercent < 0.01 ? "+0%" : $"+{BoostPercent:0}%";

    public string StateLabel
    {
        get => _stateLabel;
        set
        {
            if (_stateLabel == value) return;
            _stateLabel = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal static class AudioBoostMath
{
    public const double MaxBoostPercent = 1000;
    public const double MaxGainDb = 20;

    public static double ClampPercent(double percent) => Math.Clamp(percent, 0, MaxBoostPercent);

    // 100% means 2x amplitude, and 1000% means 10x amplitude (+20 dB).
    public static double PercentToDb(double percent) =>
        20 * Math.Log10(1 + ClampPercent(percent) / 100);

    public static double DbToPercent(double gainDb) =>
        (Math.Pow(10, Math.Clamp(gainDb, 0, MaxGainDb) / 20) - 1) * 100;
}
