using Microsoft.Win32;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace SpeedMonitor
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : System.Windows.Window
    {
        private PerformanceCounter? dlCounter;
        private PerformanceCounter? ulCounter;
        private DispatcherTimer? timer;
        private string? selectedInstanceName;
        
        public MainWindow()
        {
            InitializeComponent();
            CheckCurrentStartupStatus();                               
            SetupNetworkCounters();
            SetupTimer();

            // Enable dragging the compact container anywhere on screen
            this.MouseDown += (s, e) =>
            {
                if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                {
                    this.DragMove();
                }
            };
        }
        private void CheckCurrentStartupStatus()
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (key != null)
                    {
                    StartupMenuItem.IsChecked = key.GetValue("SpeedMonitor") != null;
                    }
                }
            }
            catch (Exception ex)
            {
            Debug.WriteLine("Failed to read startup registry:" + ex.Message);
            }
        }

        // Event: User turns "Start with Windows" ON
        private void Startup_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        // Gets the path of your running executable
                        string exePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
                        key.SetValue("SpeedMonitor", $"\"{exePath}\"");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Could not set startup settings: {ex.Message}");
            }
        }

        // Event: User turns "Start with Windows" OFF
        private void Startup_Unchecked(object sender, RoutedEventArgs e)
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    key?.DeleteValue("SpeedMonitor", false); // false means don't throw error if it's already missing
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Could not remove startup settings: {ex.Message}");
            }
        }

        // Event: User clicks Exit from the context menu
        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Application.Current.Shutdown();
        }

        private void DisplayMode_Click(object sender, RoutedEventArgs e)
        {
            // Prevent execution if labels haven't fully initialized yet
            if (DlLabel == null || UlLabel == null) return;

            System.Windows.Controls.MenuItem clickedItem = (System.Windows.Controls.MenuItem)sender;

            // 1. Enforce Radio Button behavior (uncheck everything else)
            MenuShowBoth.IsChecked = (clickedItem == MenuShowBoth);
            MenuShowDl.IsChecked = (clickedItem == MenuShowDl);
            MenuShowUl.IsChecked = (clickedItem == MenuShowUl);

            // 2. Adjust visibility of UI text elements based on selection
            if (MenuShowBoth.IsChecked)
            {
                DlLabel.Visibility = Visibility.Visible;
                UlLabel.Visibility = Visibility.Visible;
            }
            else if (MenuShowDl.IsChecked)
            {
                DlLabel.Visibility = Visibility.Visible;
                UlLabel.Visibility = Visibility.Collapsed; // Collapsed frees up layout space entirely
            }
            else if (MenuShowUl.IsChecked)
            {
                DlLabel.Visibility = Visibility.Collapsed;
                UlLabel.Visibility = Visibility.Visible;
            }
        }        

        private void SetupNetworkCounters()
        {
            try
            {
                var category = new PerformanceCounterCategory("Network Interface");
                var instanceNames = category.GetInstanceNames();
                string? targetInstance = null;

                // Get all network interfaces that are physically UP and active
                var activeInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(a => a.OperationalStatus == OperationalStatus.Up &&
                                a.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .ToList();

                // Prioritize Wi-Fi if it's connected, otherwise fallback to Ethernet
                var preferredInterface = activeInterfaces.FirstOrDefault(a => a.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                                         ?? activeInterfaces.FirstOrDefault(a => a.NetworkInterfaceType == NetworkInterfaceType.Ethernet);

                if (preferredInterface != null )
                {
                    // Windows Performance Counters replace special characters like '(', ')', '#', '/', '\' with underscores '_'.
                    // We need to sanitize the interface Description to match the Performance Counter instance name style.
                    string sanitizedDescription = preferredInterface.Description
                        .Replace('(', '_')
                        .Replace(')', '_')
                        .Replace('#', '_')
                        .Replace('/', '_')
                        .Replace('\\', '_');

                    // Find the instance name that contains our sanitized adapter description
                    targetInstance = instanceNames.FirstOrDefault(n => n.IndexOf(sanitizedDescription, StringComparison.OrdinalIgnoreCase) >= 0);

                    // Micro-fallback: Try matching via the basic Name (e.g., "Wi-Fi" or "Ethernet") if Description fails
                    if (targetInstance == null)
                    {
                        targetInstance = instanceNames.FirstOrDefault(n => n.IndexOf(preferredInterface.Name, StringComparison.OrdinalIgnoreCase) >= 0);
                    }
                }

                if (targetInstance == null)
                {
                    targetInstance = instanceNames.FirstOrDefault(name => name.Contains("Wi-Fi") || name.Contains("WiFi") || name.Contains("Wireless"))
                             ?? instanceNames.FirstOrDefault(name => name.Contains("Ethernet"))
                             ?? (instanceNames.Length > 0 ? instanceNames[0] : null);
                }

                if (targetInstance != null)
                {
                    selectedInstanceName = targetInstance;
                    Debug.WriteLine("Successfully locked onto active adapter: " + selectedInstanceName);

                    dlCounter = new PerformanceCounter("Network Interface", "Bytes Received/sec", targetInstance, true);
                    ulCounter = new PerformanceCounter("Network Interface", "Bytes Sent/sec", targetInstance, true);

                    // Prime the counters
                    dlCounter.NextValue();
                    ulCounter.NextValue();
                }
                else
                {
                    System.Windows.MessageBox.Show("No suitable active network interface found.");
                }                
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error initializing counters: {ex.Message}");
            }
        }

        private void SetupTimer()
        {
            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += Timer_Tick;
            timer.Start();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (dlCounter == null || ulCounter == null) return;

            // Get current bytes per second
            float dlBytes = 0f;
            float ulBytes = 0f;
            try
            {
                dlBytes = dlCounter.NextValue();
                ulBytes = ulCounter.NextValue();
                Debug.WriteLine($"Tick ({selectedInstanceName ?? "?"}): dl={dlBytes}, ul={ulBytes}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error reading counters: " + ex.Message);
                // show an error state in the UI
                DlLabel.Text = "Download: error";
                UlLabel.Text = "Upload: error";
                return;
            }

            // Show raw bytes per second
            double dlBps = dlBytes;
            double ulBps = ulBytes;

            // Choose display unit based on magnitude: B/s, KB/s, or MB/s
            static string FormatSpeed(double bytesPerSec)
            {
                if (bytesPerSec >= 1_000_000)
                    return $"{bytesPerSec / 1_000_000.0:F2} MB/s";
                if (bytesPerSec >= 1_000)
                    return $"{bytesPerSec / 1_000.0:F2} KB/s";
                return $"{bytesPerSec:N0} B/s";
            }

            DlLabel.Text = $"↓ {FormatSpeed(dlBps)}";
            UlLabel.Text = $"↑ {FormatSpeed(ulBps)}";
        }
    }
}