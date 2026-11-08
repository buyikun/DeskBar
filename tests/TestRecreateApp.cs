
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
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Window win = null;

        Action createWin = () => {
            win = new Window {
                Title = "RecreatedOwnedWpfWin",
                Width = 200,
                Height = 100,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };
            win.SourceInitialized += (s, e) => {
                var hwnd = new WindowInteropHelper(win).Handle;
                Console.WriteLine("New Win HWND: " + hwnd);
                SetWindowLongPtr(hwnd, -8, dummyHwnd);
                Console.WriteLine("Owner set to: " + GetWindowLongPtr(hwnd, -8));
            };
            win.Closed += (s, e) => Console.WriteLine("Window Closed event fired!");
            win.Show();
        };

        createWin();

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (s, e) => {
            timer.Stop();
            Console.WriteLine("Destroying dummyForm...");
            dummyForm.Close();
            dummyForm.Dispose();

            // 模拟收到 TaskbarCreated 后重新创建
            var t2 = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            t2.Tick += (s2, e2) => {
                t2.Stop();
                Console.WriteLine("Creating new dummyForm (simulating explorer restart)...");
                dummyForm = new Form { Text = "NewDummyTrayForm" };
                dummyForm.Show();
                dummyHwnd = dummyForm.Handle;

                Console.WriteLine("Recreating WPF window...");
                try {
                    createWin();
                    Console.WriteLine("Recreate successful!");
                } catch (Exception ex) {
                    Console.WriteLine("Recreate failed: " + ex);
                }

                var t3 = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                t3.Tick += (s3, e3) => {
                    t3.Stop();
                    Console.WriteLine("Finished test cleanly.");
                    app.Shutdown(0);
                };
                t3.Start();
            };
            t2.Start();
        };
        timer.Start();

        app.Run();
    }
}
