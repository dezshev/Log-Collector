using System;
using System.Windows;

namespace LogCollector.AdminWpf
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application app = new Application();
            app.Run(new MainWindow());
        }
    }
}
