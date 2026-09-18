using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PCAnalyse.Core;
using PCAnalyse.Services;
using PCAnalyse.Setup;

namespace PCAnalyse;

public partial class MainWindow : Window
{
    private readonly PcRegistry _registry;
    private readonly PairingStore _pairing = new();
    private readonly string _projectRoot;
    private Button[] _navButtons = Array.Empty<Button>();
    private LinkHub? _hub;
    private IReadOnlyList<DiscoveredPeer> _peers = Array.Empty<DiscoveredPeer>();

    public MainWindow()
    {
        InitializeComponent();
        _projectRoot = DetectProjectRoot();
        _registry = new PcRegistry(PcRegistry.DefaultFilePath);
        _navButtons = new[] { NavOverviewButton, NavSecondPcButton, NavLinkButton, NavInfoButton };
        Loaded += async (_, _) =>
        {
            LoadLocalInfo();
            StartLink();
            try { await UpdateUi.CheckAsync(this, UpdateChannel.Main, showIfCurrent: false); }
            catch { /* stiller Start, manuell über Update */ }
        };
        Closed += (_, _) => _hub?.Dispose();
    }

    private static string DetectProjectRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            if (File.Exists(Path.Combine(dir, "PC_Analyse.sln")) || Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName ?? dir;
        }

        return @"Z:\PC_Analyse";
    }

    private void LoadLocalInfo()
    {
        var info = LocalMachineInfo.Collect();
        LocalNameText.Text = info.ComputerName;
        LocalUserText.Text = info.UserName;
        LocalOsText.Text = info.OperatingSystem;
        LocalCpuText.Text = info.ProcessorSummary;
        LocalRamText.Text = info.MemorySummary;
        LocalNetText.Text = info.NetworkSummary;
        LocalDiskText.Text = info.DiskSummary;
        DisplayNameBox.Text = info.ComputerName;
        HostnameBox.Text = info.ComputerName;
        ProjectPathBox.Text = _projectRoot;
        SidebarVersionText.Text = "PC Analyse " + AppInfo.DisplayVersion;
        if (InfoVersionText is not null)
            InfoVersionText.Text = "Version " + AppInfo.DisplayVersion + "  ·  GitHub " + AppInfo.GitHubOwner + "/" + AppInfo.GitHubRepo;
        RefreshPcList();
        SetStatus("Bereit");
    }

    private void RefreshPcList()
    {
        var machines = _registry.Load();
        PcList.Items.Clear();
        foreach (var pc in machines)
        {
            var worker = pc.IsLocalWorker ? "  ·  Agent-Worker" : "";
            PcList.Items.Add($"{pc.DisplayName}  ·  {pc.Hostname}{worker}");
        }

        PcCountText.Text = machines.Count == 1 ? "1 Rechner angelegt" : $"{machines.Count} Rechner angelegt";
        TryWriteAgentFile(machines);
    }

    private void TryWriteAgentFile(IReadOnlyList<RegisteredPc> machines)
    {
        try
        {
            var cursorDir = Path.Combine(_projectRoot, ".cursor");
            Directory.CreateDirectory(cursorDir);
            var lines = machines.Select(m =>
                $"{m.DisplayName}\t{m.Hostname}\t{m.ProjectPath}\t{(m.IsLocalWorker ? "worker" : "inventory")}");
            File.WriteAllLines(Path.Combine(cursorDir, "registered-pcs.txt"), lines);
        }
        catch
        {
            // Agent-Datei ist optional.
        }
    }

    private void ShowPanel(Grid panel, Button active)
    {
        OverviewPanel.Visibility = panel == OverviewPanel ? Visibility.Visible : Visibility.Collapsed;
        SecondPcPanel.Visibility = panel == SecondPcPanel ? Visibility.Visible : Visibility.Collapsed;
        LinkPanel.Visibility = panel == LinkPanel ? Visibility.Visible : Visibility.Collapsed;
        InfoPanel.Visibility = panel == InfoPanel ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in _navButtons)
            button.Style = (Style)FindResource(button == active ? "SidebarBtnActive" : "SidebarBtn");
    }

    private void NavOverview_Click(object sender, RoutedEventArgs e) => ShowPanel(OverviewPanel, NavOverviewButton);
    private void NavSecondPc_Click(object sender, RoutedEventArgs e) => ShowPanel(SecondPcPanel, NavSecondPcButton);
    private void NavLink_Click(object sender, RoutedEventArgs e) => ShowPanel(LinkPanel, NavLinkButton);
    private void NavInfo_Click(object sender, RoutedEventArgs e) => ShowPanel(InfoPanel, NavInfoButton);

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("Suche GitHub-Update …");
        await UpdateUi.CheckAsync(this, UpdateChannel.Main, showIfCurrent: true);
        SetStatus("Bereit");
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        try { DragMove(); }
        catch { /* ignorieren, wenn Drag nicht möglich */ }
    }

    private void TitleBarMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void TitleBarMaximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void TitleBarClose_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private bool TryReadForm(out RegisteredPc machine, out string error)
    {
        machine = new RegisteredPc();
        if (!HostnameValidator.TryNormalize(HostnameBox.Text, out var host, out error))
            return false;

        var name = string.IsNullOrWhiteSpace(DisplayNameBox.Text) ? host : DisplayNameBox.Text.Trim();
        var project = string.IsNullOrWhiteSpace(ProjectPathBox.Text) ? _projectRoot : ProjectPathBox.Text.Trim();
        machine = new RegisteredPc
        {
            DisplayName = name,
            Hostname = host,
            ProjectPath = project,
            IsLocalWorker = false
        };
        return true;
    }

    private async void Ping_Click(object sender, RoutedEventArgs e)
    {
        if (!HostnameValidator.TryNormalize(HostnameBox.Text, out var host, out var error))
        {
            SetStatus(error);
            return;
        }

        SetStatus($"Prüfe {host} …");
        var reachable = await Task.Run(() =>
        {
            try
            {
                using var ping = new Ping();
                var reply = ping.Send(host, 2000);
                return reply.Status == IPStatus.Success;
            }
            catch
            {
                return false;
            }
        });

        SetStatus(reachable ? $"{host} ist erreichbar." : $"{host} antwortet nicht.");
    }

    private void BrowseProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Projektordner wählen",
            InitialDirectory = ProjectPathBox.Text
        };
        if (dialog.ShowDialog() == true)
            ProjectPathBox.Text = dialog.FolderName;
    }

    private void SavePc_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadForm(out var machine, out var error))
        {
            SetStatus(error);
            return;
        }

        _registry.AddOrUpdate(machine);
        RefreshPcList();
        SetStatus($"{machine.DisplayName} gespeichert. Auf dem zweiten PC «Für den Agent anlegen» starten.");
    }

    private void RegisterLocalWorker_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadForm(out var machine, out var error))
        {
            SetStatus(error);
            return;
        }

        machine.DisplayName = string.IsNullOrWhiteSpace(DisplayNameBox.Text)
            ? Environment.MachineName
            : DisplayNameBox.Text.Trim();
        machine.Hostname = Environment.MachineName;
        machine.IsLocalWorker = true;
        HostnameBox.Text = machine.Hostname;
        DisplayNameBox.Text = machine.DisplayName;
        _registry.AddOrUpdate(machine);
        RefreshPcList();

        try
        {
            CursorWorkerLauncher.Start(machine.DisplayName, machine.ProjectPath);
            SetStatus($"Worker gestartet: {machine.DisplayName}. Fenster offen lassen.");
        }
        catch (Exception ex)
        {
            SetStatus("Worker konnte nicht starten: " + ex.Message);
        }
    }

    private void StartLink()
    {
        _hub = new LinkHub(_pairing) { Waiting = true };
        _hub.StatusChanged += text => Dispatcher.Invoke(() =>
        {
            SetStatus(text);
            if (LinkStatusDetail is not null)
                LinkStatusDetail.Text = text;
        });
        _hub.PeerConnected += (_, name) => Dispatcher.Invoke(() =>
        {
            _registry.AddOrUpdate(new RegisteredPc
            {
                DisplayName = name,
                Hostname = name,
                ProjectPath = _projectRoot,
                IsLocalWorker = false
            });
            RefreshPcList();
        });
        _hub.PeersChanged += peers => Dispatcher.Invoke(() => OnPeers(peers));
        _hub.Start();
    }

    private void OnPeers(IReadOnlyList<DiscoveredPeer> peers)
    {
        _peers = peers;
        PeerList.Items.Clear();
        foreach (var peer in peers)
        {
            var wait = peer.Waiting ? "bereit" : "sichtbar";
            PeerList.Items.Add($"{peer.ComputerName}  ·  {peer.EndPoint.Address}  ·  {wait}");
        }

        if (PeerList.Items.Count > 0 && PeerList.SelectedIndex < 0)
            PeerList.SelectedIndex = 0;
    }

    private async void ConnectPeer_Click(object sender, RoutedEventArgs e)
    {
        if (PeerList.SelectedIndex < 0 || PeerList.SelectedIndex >= _peers.Count)
        {
            SetStatus("Kein Rechner gefunden. Verbindung auf dem zweiten PC starten.");
            return;
        }

        await ConnectAsync(_peers[PeerList.SelectedIndex]);
    }

    private async Task ConnectAsync(DiscoveredPeer peer)
    {
        if (_hub is null)
            return;

        SetStatus($"Verbinde mit {peer.ComputerName} …");
        try
        {
            var response = await _hub.ConnectAsync(peer);

            if (response.Type != "paired")
            {
                SetStatus(string.IsNullOrWhiteSpace(response.Reason)
                    ? "Verbindung abgelehnt."
                    : response.Reason);
                return;
            }

            _pairing.SaveToken(peer.InstanceId, response.Token);
            _registry.AddOrUpdate(new RegisteredPc
            {
                DisplayName = response.Name,
                Hostname = peer.ComputerName,
                ProjectPath = _projectRoot
            });
            RefreshPcList();
            SetStatus($"Verbunden mit {response.Name} – ohne Passwort.");
            LinkStatusDetail.Text = $"Verbunden mit {response.Name}";
        }
        catch (Exception ex)
        {
            SetStatus("Verbindung fehlgeschlagen: " + ex.Message);
        }
    }
}
