using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace AppVolumeBoost;

public sealed class AudioAppViewModel : INotifyPropertyChanged
{
    private double _boostDb;
    private string _stateLabel = "通常音量";

    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required string DisplayName { get; init; }
    public required NAudio.CoreAudioApi.AudioSessionControl Session { get; init; }
    public ImageSource? Icon { get; init; }

    public string ProcessLabel => $"{ProcessName}.exe  ·  PID {ProcessId}";

    public double BoostDb
    {
        get => _boostDb;
        set
        {
            var clamped = Math.Clamp(value, 0, 12);
            if (Math.Abs(_boostDb - clamped) < 0.01) return;
            _boostDb = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BoostLabel));
        }
    }

    public string BoostLabel => BoostDb < 0.01 ? "+0 dB" : $"+{BoostDb:0.0} dB";

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
