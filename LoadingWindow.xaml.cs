
using System.ComponentModel;
using System.Windows;

namespace HLAImputation
{
    public partial class LoadingWindow : Window
    {
        private bool _allowClose = false;

        public LoadingWindow()
        {
            InitializeComponent();
        }

        // ✅ Prevent user from closing early
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
            }
        }

        // ✅ Only allow programmatic close
        public void AllowClose()
        {
            _allowClose = true;
        }

        public void UpdateStatus(string message)
        {
            Dispatcher.Invoke(() => StatusText.Text = message);
        }

        public void UpdateReferenceProgress(double percent)
        {
            Dispatcher.Invoke(() => RefProgress.Value = percent);
        }

        public void UpdateDbProgress(double percent)
        {
            Dispatcher.Invoke(() => DbProgress.Value = percent);
        }
    }
}
