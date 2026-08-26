using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DragonQuotaWidget;

public partial class SettingsWindow : Window
{
    public SettingsWindow(WidgetSettings settings)
    {
        InitializeComponent();
        ScaleSlider.Value = settings.Scale;
        LeftClickModeComboBox.SelectedIndex = settings.LeftClickMode switch
        {
            LeftClickDisplayMode.CodexQuota => 0,
            LeftClickDisplayMode.AgyQuota => 1,
            _ => 2
        };
        TokenTimeRangeComboBox.SelectedIndex = settings.TokenTimeRange == TokenTimeRange.Last24Hours ? 1 : 0;
        SummaryTimeRangeComboBox.SelectedIndex = settings.SummaryTimeRange switch
        {
            SummaryTimeRange.Last30Days => 1,
            SummaryTimeRange.AllTime => 2,
            _ => 0
        };
        SoundEnabledCheckBox.IsChecked = settings.SoundEnabled;
        SoundSetComboBox.SelectedIndex = settings.SoundSet == InteractionSoundSet.Effect1 ? 1 : 0;
        VolumeSlider.Value = settings.SoundVolume;
        ResetLockSlider.Value = settings.ResetInteractionLockSeconds;
        InfoPanelDurationSlider.Value = settings.InfoPanelDisplaySeconds;
        ShowCodexActivityBubbleCheckBox.IsChecked = settings.ShowCodexActivityBubble;
        LockPositionCheckBox.IsChecked = settings.LockPosition;
        AlwaysOnTopCheckBox.IsChecked = settings.AlwaysOnTop;
        AttachToCodexCheckBox.IsChecked = settings.AttachToCodex;
        StartWithCodexCheckBox.IsChecked = settings.StartWithCodex;
        MinimizeOnCloseCheckBox.IsChecked = settings.MinimizeOnClose;
        UpdateSizeText();
        UpdateInteractionText();
    }

    public void ApplyTo(WidgetSettings settings)
    {
        settings.Scale = ScaleSlider.Value;
        settings.LeftClickMode = LeftClickModeComboBox.SelectedIndex switch
        {
            0 => LeftClickDisplayMode.CodexQuota,
            1 => LeftClickDisplayMode.AgyQuota,
            _ => LeftClickDisplayMode.Interaction
        };
        if (settings.LeftClickMode == LeftClickDisplayMode.CodexQuota)
        {
            settings.UsageSource = UsageSource.Codex;
        }
        else if (settings.LeftClickMode == LeftClickDisplayMode.AgyQuota)
        {
            settings.UsageSource = UsageSource.Agy;
        }
        settings.TokenTimeRange = TokenTimeRangeComboBox.SelectedIndex == 1 ? TokenTimeRange.Last24Hours : TokenTimeRange.Today;
        settings.SummaryTimeRange = SummaryTimeRangeComboBox.SelectedIndex switch
        {
            1 => SummaryTimeRange.Last30Days,
            2 => SummaryTimeRange.AllTime,
            _ => SummaryTimeRange.Last7Days
        };
        settings.SoundEnabled = SoundEnabledCheckBox.IsChecked == true;
        settings.SoundSet = SoundSetComboBox.SelectedIndex == 1 ? InteractionSoundSet.Effect1 : InteractionSoundSet.Duck;
        settings.SoundVolume = VolumeSlider.Value;
        settings.ResetInteractionLockSeconds = ResetLockSlider.Value;
        settings.InfoPanelDisplaySeconds = InfoPanelDurationSlider.Value;
        settings.ShowCodexActivityBubble = ShowCodexActivityBubbleCheckBox.IsChecked == true;
        settings.ShowInfoPanel = false;
        settings.LockPosition = LockPositionCheckBox.IsChecked == true;
        settings.AlwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true;
        settings.AttachToCodex = AttachToCodexCheckBox.IsChecked == true;
        settings.StartWithCodex = StartWithCodexCheckBox.IsChecked == true;
        settings.MinimizeOnClose = MinimizeOnCloseCheckBox.IsChecked == true;
    }

    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSizeText();
    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateInteractionText();
    private void ResetLockSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateInteractionText();
    private void InfoPanelDurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateInteractionText();

    private void UpdateSizeText()
    {
        if (ScaleValueText is null || DimensionText is null) return;
        var scale = ScaleSlider.Value;
        const double baseWidth = 268d;
        const double baseHeight = 300d;
        ScaleValueText.Text = $"{scale * 100:0}%";
        DimensionText.Text = $"约 {baseWidth * scale:0} × {baseHeight * scale:0} 像素";
    }

    private void UpdateInteractionText()
    {
        if (VolumeValueText is null || ResetLockValueText is null || InfoPanelDurationValueText is null) return;
        VolumeValueText.Text = $"{VolumeSlider.Value * 100:0}%";
        ResetLockValueText.Text = $"{ResetLockSlider.Value:0} 秒";
        InfoPanelDurationValueText.Text = $"{InfoPanelDurationSlider.Value:0} 秒";
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    public void SavePreview(string path)
    {
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
        encoder.Save(stream);
    }
}
