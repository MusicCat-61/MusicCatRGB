using System;
using System.Windows;
using System.Windows.Threading;

namespace MusicCat.CLVKM.UI;

public partial class EffectToastWindow : Window
{
    private readonly DispatcherTimer _closeTimer;

    public EffectToastWindow()
    {
        InitializeComponent();

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            Hide();
        };
    }

    public void ShowEffect(string name, string description)
    {
        EffectNameText.Text = name;
        EffectDescriptionText.Text = description;

        Rect workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 24;
        Top = workArea.Bottom - Height - 24;

        _closeTimer.Stop();
        _closeTimer.Start();

        if (!IsVisible)
        {
            Show();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closeTimer.Stop();
        base.OnClosed(e);
    }
}
