using System.Windows;
using System.Windows.Input;
using MusicCat.CLVKM.UI.Localization;

namespace MusicCat.CLVKM.UI
{
    public partial class ModernMessageBox : Window
    {
        public bool Result { get; private set; }

        public ModernMessageBox(string message, string title, bool showYesNo)
        {
            InitializeComponent();

            TitleText.Text = title;
            MessageText.Text = message;

            if (showYesNo)
            {
                BtnYes.Content = Loc.T("Yes");
                BtnNo.Content = Loc.T("No");
            }
            else
            {
                BtnYes.Content = Loc.T("Ok");
                BtnNo.Visibility = Visibility.Collapsed;
            }
        }

        public static bool Show(string message, string title) =>
            Run(new ModernMessageBox(message, title, showYesNo: true));

        public static void ShowInfo(string message, string title) =>
            Run(new ModernMessageBox(message, title, showYesNo: false));

        private static bool Run(ModernMessageBox box)
        {
            box.ShowDialog();
            return box.Result;
        }

        private void BtnYes_Click(object sender, RoutedEventArgs e)
        {
            Result = true;
            Close();
        }

        private void BtnNo_Click(object sender, RoutedEventArgs e)
        {
            Result = false;
            Close();
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }
    }
}
