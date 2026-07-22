using CommunityToolkit.Mvvm.ComponentModel;

namespace ResourceMonitor.Gui.ViewModels;

public sealed partial class DiskThresholdRow : ObservableObject
{
    public string DriveName { get; }

    [ObservableProperty] private double minFreePercent;

    public DiskThresholdRow(string driveName, double minFreePercent)
    {
        DriveName = driveName;
        MinFreePercent = minFreePercent;
    }
}
