using System.Windows;
using AudioCompressor.ViewModels;

namespace AudioCompressor.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    var vm = this.DataContext as MainViewModel;
                    vm?.LoadAudioFromPath(files[0]);
                }
            }
        }
    }
}