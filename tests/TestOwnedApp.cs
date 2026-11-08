
using System;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public class App {
    [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr h);

    [STAThread]
    public static void Main() {
        var dummyForm = new Form { Text = "DummyTrayForm" };
        dummyForm.Show();
        var dummyHwnd = dummyForm.Handle;

        var app = new System.Windows.Application();
        var win = new Window {
            Title = "TestOwnedWpfWin",
            Width = 200,
            Height = 100,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false
        };
        win.SourceInitialized += (s, e) => {
            var hwnd = new WindowInteropHelper(win).Handle;
            Console.WriteLine("DummyParent HWND: " + dummyHwnd);
            SetWindowLongPtr(hwnd, -8, dummyHwnd);
            Console.WriteLine("Owner set to: " + GetWindowLongPtr(hwnd, -8));

            win.Closed += (s2, e2) => Console.WriteLine("WPF Window Closed event fired!");

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s3, e3) => {
                timer.Stop();
                Console.WriteLine("Closing dummy form (destroying owner)...");
                dummyForm.Close();
                dummyForm.Dispose();
                
                Console.WriteLine("After owner destroy:");
                Console.WriteLine("IsWindow(wpfHwnd): " + IsWindow(hwnd));
                Console.WriteLine("win.IsLoaded: " + win.IsLoaded);
                
                var t2 = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                t2.Tick += (s4, e4) => {
                    t2.Stop();
                    Console.WriteLine("App alive check: IsWindow=" + IsWindow(hwnd) + " IsLoaded=" + win.IsLoaded);
                    app.Shutdown(0);
                };
                t2.Start();
            };
            timer.Start();
        };
        app.Run(win);
    }
}
