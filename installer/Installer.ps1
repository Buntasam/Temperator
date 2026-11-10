# Installeur de Temperator (interface graphique WPF, exécutée par Windows PowerShell, signé Microsoft).
# Embarqué dans Temperator-Setup.exe (voir build.ps1), qui le lance en administrateur.
# Peut aussi être lancé directement, à côté de Temperator.zip : il se relance alors tout seul en administrateur.
#
#   (sans option)            interface d'installation
#   -Uninstall               interface de désinstallation (utilisé par Paramètres > Applications)
#   -Silent [...]            sans interface (tests) : -Name X -NoAutoStart -NoDesktop -NoLaunch -KeepSettings
#   -Preview <dossier>       rend chaque écran en PNG, sans rien installer
param(
    [switch]$Uninstall, [switch]$Silent, [string]$Name = 'Temperator',
    [switch]$NoAutoStart, [switch]$NoDesktop, [switch]$NoLaunch, [switch]$KeepSettings,
    [string]$Preview,
    [switch]$AutoTest   # tests : l'interface clique "Installer"/"Désinstaller" toute seule puis se ferme
)
$Version = '1.0.0'   # remplacé par build.ps1 avec la version du projet
$Payload = Join-Path $PSScriptRoot 'Temperator.zip'      # l'application (à côté de ce script)

# ------------------------------------------------------------------ droits administrateur
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $Preview) {
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', "`"$PSCommandPath`"")
    foreach ($k in $PSBoundParameters.Keys) {
        $v = $PSBoundParameters[$k]
        if ($v -is [switch]) { if ($v) { $a += "-$k" } } else { $a += "-$k"; $a += "`"$v`"" }
    }
    try { Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList $a } catch { }
    exit
}

# ================================================================== moteur (sans interface)
# Défini en texte pour tourner aussi dans un runspace séparé (l'interface reste fluide).
$Engine = {
    $ErrorActionPreference = 'Stop'

    function Dir-Of($n) { Join-Path $env:ProgramFiles $n }
    $DataDir = Join-Path $env:ProgramData 'Temperator'
    function Lnk-Menu($n) { Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\$n.lnk" }
    function Lnk-Desk($n) { Join-Path $env:PUBLIC "Desktop\$n.lnk" }
    function Key-Of($n) { "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$n" }
    $DotNet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'

    function Test-Runtime {
        if (-not (Test-Path $DotNet)) { return $false }
        $ErrorActionPreference = 'Continue'
        # .NET 10 ou plus récent (l'application accepte les versions majeures suivantes)
        [bool](& $DotNet --list-runtimes | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App (\d+)\.' -and [int]$Matches[1] -ge 10 })
    }
    function Test-PawnIO { Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\PawnIO' }

    # Ferme Temperator : d'abord poliment (il enregistre ses réglages et retire son icône), sinon de force.
    function Stop-Temperator($onlyFrom) {
        $quit = $null
        if ([Threading.EventWaitHandle]::TryOpenExisting('Global\Temperator.Quit', [ref]$quit)) {
            [void]$quit.Set(); $quit.Dispose()
            for ($i = 0; $i -lt 20 -and (Get-Process Temperator -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 150 }
        }
        Get-Process Temperator -ErrorAction SilentlyContinue |
            Where-Object { -not $onlyFrom -or $_.Path -like "$onlyFrom*" } |
            Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 300
    }

    function Invoke-Winget($id) {
        $w = Get-Command winget.exe -ErrorAction SilentlyContinue
        if (-not $w) { return }
        $p = Start-Process $w.Source -ArgumentList "install --id $id -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity" -WindowStyle Hidden -Wait -PassThru
    }
    function Invoke-Download($url, $file) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $wc = New-Object Net.WebClient
        $wc.Headers.Add('User-Agent', "Temperator-Setup/$Version")
        $wc.DownloadFile($url, $file)
    }

    # .NET 10 Desktop Runtime (Microsoft) : winget, sinon le lien officiel de Microsoft
    function Install-Runtime {
        Invoke-Winget 'Microsoft.DotNet.DesktopRuntime.10'
        if (Test-Runtime) { return }
        $f = Join-Path $env:TEMP 'temperator-dotnet10.exe'
        try {
            Invoke-Download 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe' $f
            Start-Process $f -ArgumentList '/install /quiet /norestart' -Wait -WindowStyle Hidden
        } catch { } finally { Remove-Item $f -Force -ErrorAction SilentlyContinue }
    }

    # Pilote PawnIO (namazso, signé) : winget, sinon l'installeur officiel (-install -silent)
    function Install-PawnIO {
        Invoke-Winget 'namazso.PawnIO'
        if (Test-PawnIO) { return }
        $f = Join-Path $env:TEMP 'temperator-pawnio.exe'
        try {
            Invoke-Download 'https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe' $f
            Start-Process $f -ArgumentList '-install -silent' -Wait -WindowStyle Hidden
            if (-not (Test-PawnIO)) { Start-Process $f -Wait }   # dernier recours : son installeur à l'écran
        } catch { } finally { Remove-Item $f -Force -ErrorAction SilentlyContinue }
    }

    function New-Shortcut($lnk, $dir, $task) {
        $s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
        $s.TargetPath = "$env:WINDIR\System32\conhost.exe"            # lance sans fenêtre de console
        $s.Arguments = "--headless schtasks.exe /run /tn `"$task`""    # via la tâche : admin sans demande UAC
        $s.WorkingDirectory = "$dir\app"
        $s.IconLocation = "$dir\app\Temperator.exe,0"
        $s.Description = 'Temperator - températures et utilisation du PC'
        $s.Save()
    }

    function Install-Temperator($o, $Report) {
        $dir = Dir-Of $o.Name; $app = Join-Path $dir 'app'
        & $Report 0.03 'Préparation'
        if (-not [Environment]::Is64BitOperatingSystem -or [Environment]::OSVersion.Version.Major -lt 10) { throw 'Temperator nécessite Windows 10 ou 11 en 64 bits.' }
        if (-not (Test-Path $o.Payload)) { throw "Fichier introuvable : $($o.Payload). Télécharge à nouveau Temperator-Setup.exe." }
        Stop-Temperator $null

        if (-not (Test-Runtime)) {
            & $Report 0.08 'Installation de .NET 10 (Microsoft, environ 57 Mo)'
            Install-Runtime
            if (-not (Test-Runtime)) { throw "Impossible d'installer .NET 10. Vérifie ta connexion internet, ou installe « .NET Desktop Runtime 10 (x64) » depuis dotnet.microsoft.com, puis relance l'installeur." }
        }

        & $Report 0.45 'Copie des fichiers'
        if (Test-Path $app) { Remove-Item $app -Recurse -Force }     # pas de vieilles DLL
        New-Item -ItemType Directory -Force $app | Out-Null
        Expand-Archive -Path $o.Payload -DestinationPath $app -Force
        Copy-Item $o.Self (Join-Path $dir 'Temperator-Setup.ps1') -Force
        Copy-Item $o.Payload (Join-Path $dir 'Temperator.zip') -Force   # permet de relancer l'installeur installé
        Get-ChildItem $dir -Recurse -File | Unblock-File
        New-Item -ItemType Directory -Force $DataDir | Out-Null
        & icacls.exe $DataDir /grant '*S-1-5-32-545:(OI)(CI)M' /Q | Out-Null   # réglages modifiables par tous

        & $Report 0.6 'Démarrage et raccourcis'
        $user = (Get-CimInstance Win32_ComputerSystem).UserName
        if (-not $user) { $user = "$env:USERDOMAIN\$env:USERNAME" }
        $action = New-ScheduledTaskAction -Execute "$app\Temperator.exe" -WorkingDirectory $app
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
        # Parallel : relancer depuis le raccourci démarre une 2e instance qui demande à la 1re de se réafficher, puis s'arrête
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances Parallel
        $taskArgs = @{ TaskName = $o.Name; Action = $action; Principal = $principal; Settings = $settings; Description = "Temperator $Version"; Force = $true }
        if ($o.AutoStart) { $t = New-ScheduledTaskTrigger -AtLogOn -User $user; $t.Delay = 'PT15S'; $taskArgs.Trigger = $t }
        Register-ScheduledTask @taskArgs | Out-Null      # toujours créée : le raccourci s'en sert
        New-Shortcut (Lnk-Menu $o.Name) $dir $o.Name
        if ($o.Desktop) { New-Shortcut (Lnk-Desk $o.Name) $dir $o.Name } else { Remove-Item (Lnk-Desk $o.Name) -Force -ErrorAction SilentlyContinue }

        $k = Key-Of $o.Name
        New-Item $k -Force | Out-Null
        $un = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$dir\Temperator-Setup.ps1`" -Uninstall" + $(if ($o.Name -ne 'Temperator') { " -Name `"$($o.Name)`"" } else { '' })
        $props = @{ DisplayName = 'Temperator'; DisplayVersion = $Version; Publisher = 'Temperator'; DisplayIcon = "$app\Temperator.exe"; InstallLocation = $dir; UninstallString = $un; QuietUninstallString = "$un -Silent" }
        foreach ($p in $props.Keys) { Set-ItemProperty $k -Name $p -Value $props[$p] }
        foreach ($p in 'NoModify', 'NoRepair') { Set-ItemProperty $k -Name $p -Value 1 -Type DWord }
        Set-ItemProperty $k -Name EstimatedSize -Value ([int]((Get-ChildItem $dir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)) -Type DWord

        if (-not (Test-PawnIO)) {
            & $Report 0.7 'Installation du pilote PawnIO (environ 3 Mo)'
            Install-PawnIO
            if (-not (Test-PawnIO)) { throw "Temperator est installé, mais le pilote PawnIO (obligatoire) n'a pas pu s'installer. Vérifie ta connexion internet et relance l'installeur, ou installe PawnIO depuis pawnio.eu." }
        }

        if ($o.Launch) { & $Report 0.95 'Lancement de Temperator'; Start-ScheduledTask -TaskName $o.Name }
        & $Report 1 'Terminé'
    }

    function Uninstall-Temperator($o, $Report) {
        $dir = Dir-Of $o.Name
        Set-Location $env:TEMP
        & $Report 0.15 'Arrêt de Temperator'
        Stop-Temperator "$dir\app"
        & $Report 0.45 'Suppression du démarrage et des raccourcis'
        Unregister-ScheduledTask -TaskName $o.Name -Confirm:$false -ErrorAction SilentlyContinue
        Remove-Item (Lnk-Menu $o.Name), (Lnk-Desk $o.Name) -Force -ErrorAction SilentlyContinue
        Remove-Item (Key-Of $o.Name) -Recurse -Force -ErrorAction SilentlyContinue
        if (-not $o.KeepSettings) { Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue }
        & $Report 0.75 'Suppression des fichiers'
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
        & $Report 1 'Terminé'
    }
}

$Opt = [pscustomobject]@{
    Name = $Name; AutoStart = -not $NoAutoStart; Desktop = -not $NoDesktop; Launch = -not $NoLaunch
    KeepSettings = [bool]$KeepSettings; Payload = $Payload; Self = $PSCommandPath
}

# ================================================================== mode silencieux (tests)
if ($Silent) {
    . $Engine
    $log = Join-Path $env:TEMP 'temperator-setup.log'
    $Report = { param($p, $s) Add-Content $log ("{0} {1,3:0}% {2}" -f (Get-Date -Format HH:mm:ss), ($p * 100), $s) -Encoding UTF8 }
    try {
        & $Report 0 "v$Version $(if ($Uninstall) { 'désinstallation' } else { 'installation' }) '$Name' auto=$($Opt.AutoStart) bureau=$($Opt.Desktop) lancer=$($Opt.Launch)"
        if ($Uninstall) { Uninstall-Temperator $Opt $Report } else { Install-Temperator $Opt $Report }
        exit 0
    } catch { & $Report 1 "ERREUR $($_.Exception.Message)"; exit 1 }
}

# ================================================================== interface
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Installer Temperator" Width="600" Height="466" WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        ResizeMode="NoResize" WindowStartupLocation="CenterScreen" FontFamily="Segoe UI" UseLayoutRounding="True"
        TextOptions.TextFormattingMode="Display">
  <Window.Resources>
    <SolidColorBrush x:Key="Accent" Color="#4ED8C0"/>
    <SolidColorBrush x:Key="TextB" Color="#F1F3F6"/>
    <SolidColorBrush x:Key="MutedB" Color="#8E97A4"/>
    <FontFamily x:Key="TitleFont">Bahnschrift, Segoe UI</FontFamily>

    <Style x:Key="Toggle" TargetType="CheckBox">
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="CheckBox">
            <Border x:Name="Track" Width="42" Height="24" CornerRadius="12" Background="#3A3F48" Padding="3">
              <Ellipse x:Name="Knob" Width="18" Height="18" Fill="White" HorizontalAlignment="Left"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsChecked" Value="True">
                <Setter TargetName="Track" Property="Background" Value="#4ED8C0"/>
                <Setter TargetName="Knob" Property="HorizontalAlignment" Value="Right"/>
              </Trigger>
              <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.65"/><Setter Property="Cursor" Value="Arrow"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key="Primary" TargetType="Button">
      <Setter Property="Foreground" Value="#0B0D10"/>
      <Setter Property="Background" Value="#4ED8C0"/>
      <Setter Property="FontFamily" Value="{StaticResource TitleFont}"/>
      <Setter Property="FontSize" Value="15"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="B" Background="{TemplateBinding Background}" CornerRadius="10" Padding="26,10,26,11" RenderTransformOrigin="0.5,0.5">
              <Border.Effect><DropShadowEffect Color="{Binding Background.Color, RelativeSource={RelativeSource TemplatedParent}}" ShadowDepth="0" BlurRadius="18" Opacity="0.45"/></Border.Effect>
              <ContentPresenter HorizontalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="B" Property="Opacity" Value="0.88"/></Trigger>
              <Trigger Property="IsPressed" Value="True"><Setter TargetName="B" Property="RenderTransform"><Setter.Value><ScaleTransform ScaleX="0.96" ScaleY="0.96"/></Setter.Value></Setter></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key="Ghost" TargetType="Button">
      <Setter Property="Foreground" Value="#8E97A4"/>
      <Setter Property="FontFamily" Value="{StaticResource TitleFont}"/>
      <Setter Property="FontSize" Value="15"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border Background="#01FFFFFF" Padding="20,10,20,11"><ContentPresenter/></Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter Property="Foreground" Value="#F1F3F6"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key="Title" TargetType="TextBlock"><Setter Property="Foreground" Value="#F1F3F6"/><Setter Property="FontSize" Value="15"/><Setter Property="FontWeight" Value="SemiBold"/></Style>
    <Style x:Key="Sub" TargetType="TextBlock"><Setter Property="Foreground" Value="#8E97A4"/><Setter Property="FontSize" Value="12.5"/><Setter Property="TextWrapping" Value="Wrap"/></Style>
    <Style x:Key="Row" TargetType="DockPanel"><Setter Property="Margin" Value="0,0,0,14"/><Setter Property="Background" Value="#01FFFFFF"/></Style>
    <Style x:Key="Badge" TargetType="Border">
      <Setter Property="Margin" Value="9,2,0,0"/><Setter Property="Padding" Value="7,1,7,2"/><Setter Property="CornerRadius" Value="6"/>
      <Setter Property="Background" Value="#224ED8C0"/><Setter Property="BorderBrush" Value="#554ED8C0"/><Setter Property="BorderThickness" Value="1"/>
      <Setter Property="VerticalAlignment" Value="Center"/>
    </Style>
  </Window.Resources>

  <Border x:Name="Card" Margin="24" CornerRadius="18" Padding="34,30,34,28" BorderBrush="#26F1F3F6" BorderThickness="1">
    <Border.Background>
      <LinearGradientBrush StartPoint="0,0" EndPoint="0,1"><GradientStop Color="#171A21" Offset="0"/><GradientStop Color="#0C0E12" Offset="1"/></LinearGradientBrush>
    </Border.Background>
    <Border.Effect><DropShadowEffect ShadowDepth="6" BlurRadius="30" Opacity="0.55"/></Border.Effect>
    <Grid>
      <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition/></Grid.RowDefinitions>

      <!-- en-tête : logo anneau + titre -->
      <Grid Margin="0,0,0,24">
        <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition/></Grid.ColumnDefinitions>
        <Grid x:Name="Logo" Width="58" Height="58" RenderTransformOrigin="0.5,0.5">
          <Grid.RenderTransform><RotateTransform x:Name="LogoRot"/></Grid.RenderTransform>
          <Path Data="M 12.17,45.83 A 23.8,23.8 0 1 1 45.83,45.83" Stroke="#30F1F3F6" StrokeThickness="6.4" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
          <Path x:Name="LogoArc" Data="M 12.17,45.83 A 23.8,23.8 0 1 1 45.83,45.83" Stroke="#4ED8C0" StrokeThickness="6.4" StrokeStartLineCap="Round" StrokeEndLineCap="Round">
            <Path.Effect><DropShadowEffect x:Name="LogoGlow" Color="#4ED8C0" ShadowDepth="0" BlurRadius="20" Opacity="0.9"/></Path.Effect>
          </Path>
          <Ellipse x:Name="LogoDot" Width="12.8" Height="12.8" Fill="#F1F3F6"/>
          <Path x:Name="LogoMark" Stroke="#F1F3F6" StrokeThickness="4.4" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round" Visibility="Collapsed"/>
        </Grid>
        <StackPanel Grid.Column="1" Margin="18,2,24,0" VerticalAlignment="Center">
          <StackPanel Orientation="Horizontal">
            <TextBlock Text="Temperator" FontFamily="{StaticResource TitleFont}" FontSize="28" FontWeight="SemiBold" Foreground="#F1F3F6"/>
            <Border Margin="10,6,0,0" Padding="8,2,8,3" CornerRadius="8" Background="#224ED8C0" VerticalAlignment="Center">
              <TextBlock x:Name="Ver" FontFamily="{StaticResource TitleFont}" FontSize="12" FontWeight="SemiBold" Foreground="#4ED8C0"/>
            </Border>
          </StackPanel>
          <TextBlock x:Name="Subtitle" FontSize="13.5" Foreground="#8E97A4" TextWrapping="Wrap" Text="Températures et utilisation de ton PC, toujours à l'écran."/>
        </StackPanel>
        <TextBlock x:Name="CloseX" Grid.Column="1" Text="✕" FontSize="15" Foreground="#8E97A4" HorizontalAlignment="Right" VerticalAlignment="Top" Margin="0,-6,-4,0" Cursor="Hand"/>
      </Grid>

      <!-- écran : options -->
      <DockPanel x:Name="PageOptions" Grid.Row="1">
        <DockPanel DockPanel.Dock="Bottom" LastChildFill="False">
          <Button x:Name="BtnInstall" DockPanel.Dock="Right" Style="{StaticResource Primary}" Content="Installer"/>
          <Button x:Name="BtnCancel" DockPanel.Dock="Right" Style="{StaticResource Ghost}" Content="Annuler"/>
          <Button x:Name="BtnGoUninstall" DockPanel.Dock="Left" Style="{StaticResource Ghost}" Content="Désinstaller" Padding="0" Visibility="Collapsed"/>
        </DockPanel>
        <StackPanel>
          <DockPanel x:Name="RowAuto" Style="{StaticResource Row}" Cursor="Hand">
            <CheckBox x:Name="OptAuto" DockPanel.Dock="Right" Style="{StaticResource Toggle}" IsChecked="True" VerticalAlignment="Center"/>
            <StackPanel Margin="0,0,16,0"><TextBlock Style="{StaticResource Title}" Text="Lancer au démarrage de Windows"/><TextBlock Style="{StaticResource Sub}" Text="S'ouvre tout seul à chaque session, sans demande d'autorisation."/></StackPanel>
          </DockPanel>
          <DockPanel x:Name="RowDesk" Style="{StaticResource Row}" Cursor="Hand">
            <CheckBox x:Name="OptDesk" DockPanel.Dock="Right" Style="{StaticResource Toggle}" IsChecked="True" VerticalAlignment="Center"/>
            <StackPanel Margin="0,0,16,0"><TextBlock Style="{StaticResource Title}" Text="Raccourci sur le bureau"/><TextBlock Style="{StaticResource Sub}" Text="Le menu Démarrer en a toujours un."/></StackPanel>
          </DockPanel>
          <DockPanel x:Name="RowLaunch" Style="{StaticResource Row}" Cursor="Hand">
            <CheckBox x:Name="OptLaunch" DockPanel.Dock="Right" Style="{StaticResource Toggle}" IsChecked="True" VerticalAlignment="Center"/>
            <StackPanel Margin="0,0,16,0"><TextBlock Style="{StaticResource Title}" Text="Lancer Temperator à la fin"/><TextBlock Style="{StaticResource Sub}" Text="Il s'affiche en haut à droite de l'écran."/></StackPanel>
          </DockPanel>

          <!-- ce qui manque sur ce PC (affiché seulement s'il manque) -->
          <StackPanel x:Name="Missing" Visibility="Collapsed">
            <TextBlock Text="À INSTALLER AUSSI" FontFamily="{StaticResource TitleFont}" FontSize="11" FontWeight="Bold" Foreground="#4ED8C0" Margin="0,6,0,10"/>
            <DockPanel x:Name="RowDotNet" Style="{StaticResource Row}">
              <CheckBox DockPanel.Dock="Right" Style="{StaticResource Toggle}" IsChecked="True" IsEnabled="False" VerticalAlignment="Center"/>
              <StackPanel Margin="0,0,16,0">
                <StackPanel Orientation="Horizontal"><TextBlock Style="{StaticResource Title}" Text=".NET 10 Desktop Runtime"/><Border Style="{StaticResource Badge}"><TextBlock Text="OBLIGATOIRE" FontFamily="{StaticResource TitleFont}" FontSize="10.5" FontWeight="Bold" Foreground="#4ED8C0"/></Border></StackPanel>
                <TextBlock Style="{StaticResource Sub}" Text="Nécessaire pour faire tourner Temperator. Microsoft, environ 57 Mo à télécharger."/>
              </StackPanel>
            </DockPanel>
            <DockPanel x:Name="RowPawn" Style="{StaticResource Row}">
              <CheckBox DockPanel.Dock="Right" Style="{StaticResource Toggle}" IsChecked="True" IsEnabled="False" VerticalAlignment="Center"/>
              <StackPanel Margin="0,0,16,0">
                <StackPanel Orientation="Horizontal"><TextBlock Style="{StaticResource Title}" Text="Pilote PawnIO"/><Border Style="{StaticResource Badge}"><TextBlock Text="OBLIGATOIRE" FontFamily="{StaticResource TitleFont}" FontSize="10.5" FontWeight="Bold" Foreground="#4ED8C0"/></Border></StackPanel>
                <TextBlock Style="{StaticResource Sub}" Text="Pour la température du CPU et les ventilateurs. Open source et signé, environ 3 Mo."/>
              </StackPanel>
            </DockPanel>
          </StackPanel>
          <TextBlock x:Name="AllGood" Text="✓  Tout ce qu'il faut est déjà sur ce PC (.NET 10, pilote PawnIO)." FontSize="12.5" Foreground="#4ED8C0" Margin="0,0,0,6"/>
          <TextBlock x:Name="PathText" FontSize="11.5" Foreground="#8E97A4" Opacity="0.7" Margin="0,4,0,0"/>
        </StackPanel>
      </DockPanel>

      <!-- écran : progression -->
      <StackPanel x:Name="PageProgress" Grid.Row="1" Visibility="Collapsed">
        <TextBlock x:Name="Status" Style="{StaticResource Title}" Text="Préparation"/>
        <Border Height="6" CornerRadius="3" Background="#24F1F3F6" Margin="0,12,0,8">
          <Border x:Name="Fill" CornerRadius="3" Background="#4ED8C0" RenderTransformOrigin="0,0.5">
            <Border.RenderTransform><ScaleTransform x:Name="FillScale" ScaleX="0"/></Border.RenderTransform>
            <Border.Effect><DropShadowEffect Color="#4ED8C0" ShadowDepth="0" BlurRadius="10" Opacity="0.8"/></Border.Effect>
          </Border>
        </Border>
        <TextBlock x:Name="Hint" Style="{StaticResource Sub}" FontSize="12" Text="Quelques secondes, ou un peu plus s'il faut télécharger .NET 10."/>
      </StackPanel>

      <!-- écran : message final (terminé, désinstallation, erreur) -->
      <DockPanel x:Name="PageMessage" Grid.Row="1" Visibility="Collapsed">
        <DockPanel DockPanel.Dock="Bottom" LastChildFill="False">
          <Button x:Name="BtnMain" DockPanel.Dock="Right" Style="{StaticResource Primary}" Content="Terminer"/>
          <Button x:Name="BtnSecond" DockPanel.Dock="Right" Style="{StaticResource Ghost}" Content="Annuler" Visibility="Collapsed"/>
        </DockPanel>
        <TextBlock x:Name="Message" Style="{StaticResource Sub}" FontSize="13.5"/>
      </DockPanel>
    </Grid>
  </Border>
</Window>
'@

$win = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $xaml))
$ui = @{}
foreach ($n in 'Card','Logo','LogoRot','LogoArc','LogoGlow','LogoDot','LogoMark','Ver','Subtitle','CloseX','PageOptions','PageProgress','PageMessage',
               'BtnInstall','BtnCancel','BtnGoUninstall','RowAuto','RowDesk','RowLaunch','OptAuto','OptDesk','OptLaunch','Missing','RowDotNet','RowPawn',
               'AllGood','PathText','Status','Fill','FillScale','Hint','BtnMain','BtnSecond','Message') { $ui[$n] = $win.FindName($n) }
$ui.Ver.Text = "v$Version"
$ui.PathText.Text = 'Dossier : ' + (Join-Path $env:ProgramFiles $Name)

$Accent = [Windows.Media.Color]::FromRgb(0x4E, 0xD8, 0xC0)
$Danger = [Windows.Media.Color]::FromRgb(0xFF, 0x5A, 0x5F)
$Muted  = [Windows.Media.Color]::FromRgb(0x8E, 0x97, 0xA4)
function Set-LogoColor($c) {
    $ui.LogoArc.Stroke = New-Object Windows.Media.SolidColorBrush $c
    $ui.LogoGlow.Color = $c
}
function Set-Mark($geometry) {   # coche, croix ou point au centre du logo
    if ($geometry) { $ui.LogoMark.Data = [Windows.Media.Geometry]::Parse($geometry); $ui.LogoMark.Visibility = 'Visible'; $ui.LogoDot.Visibility = 'Collapsed' }
    else { $ui.LogoMark.Visibility = 'Collapsed'; $ui.LogoDot.Visibility = 'Visible' }
}
function Show-Page($page) {
    foreach ($p in 'PageOptions', 'PageProgress', 'PageMessage') { $ui[$p].Visibility = $(if ($p -eq $page) { 'Visible' } else { 'Collapsed' }) }
    $fade = New-Object Windows.Media.Animation.DoubleAnimation 0, 1, ([TimeSpan]::FromMilliseconds(220))
    $ui[$page].BeginAnimation([Windows.UIElement]::OpacityProperty, $fade)
}
function Show-Message($subtitle, $text, $color, $mark, $button, [switch]$WithCancel) {
    $ui.LogoRot.BeginAnimation([Windows.Media.RotateTransform]::AngleProperty, $null); $ui.LogoRot.Angle = 0
    $ui.LogoArc.Data = [Windows.Media.Geometry]::Parse('M 12.17,45.83 A 23.8,23.8 0 1 1 45.83,45.83')
    Set-LogoColor $color; Set-Mark $mark
    $ui.Subtitle.Text = $subtitle; $ui.Message.Text = $text
    $ui.BtnMain.Content = $button; $ui.BtnMain.Background = New-Object Windows.Media.SolidColorBrush $color
    $ui.BtnSecond.Visibility = $(if ($WithCancel) { 'Visible' } else { 'Collapsed' })
    Show-Page 'PageMessage'
}

# ce qui manque sur ce PC
. $Engine
$ErrorActionPreference = 'Continue'   # dans l'interface, une erreur ne doit jamais fermer la fenêtre
$needRuntime = -not (Test-Runtime)
$needPawn = -not (Test-PawnIO)
if ($Preview) { $needRuntime = $false; $needPawn = $false }
function Apply-Missing($rt, $pw) {
    $n = [int]$rt + [int]$pw
    $ui.Missing.Visibility = $(if ($n) { 'Visible' } else { 'Collapsed' })
    $ui.AllGood.Visibility = $(if ($n) { 'Collapsed' } else { 'Visible' })
    $ui.RowDotNet.Visibility = $(if ($rt) { 'Visible' } else { 'Collapsed' })
    $ui.RowPawn.Visibility = $(if ($pw) { 'Visible' } else { 'Collapsed' })
    $win.Height = 466 + $(if ($n) { 30 + 60 * $n } else { 0 })
}
Apply-Missing $needRuntime $needPawn

$installed = Test-Path (Join-Path (Join-Path $env:ProgramFiles $Name) 'app\Temperator.dll')
if ($installed) { $ui.BtnInstall.Content = 'Mettre à jour'; $ui.BtnGoUninstall.Visibility = 'Visible' }

# lignes cliquables : un clic n'importe où sur la ligne bascule l'interrupteur
foreach ($pair in @(@('RowAuto','OptAuto'), @('RowDesk','OptDesk'), @('RowLaunch','OptLaunch'))) {
    $cb = $ui[$pair[1]]
    $ui[$pair[0]].Add_MouseLeftButtonUp({ $cb.IsChecked = -not $cb.IsChecked }.GetNewClosure())
}

$script:busy = $false
$ui.Card.Add_MouseLeftButtonDown({ try { $win.DragMove() } catch { } })
$ui.CloseX.Add_MouseLeftButtonUp({ if (-not $script:busy) { $win.Close() } })
$win.Add_Closing({ if ($script:busy) { $_.Cancel = $true } })
$ui.BtnCancel.Add_Click({ $win.Close() })

function Show-UninstallConfirm {
    Show-Message 'Désinstaller Temperator de ce PC ?' ("Le widget, son démarrage automatique, ses raccourcis et ses réglages seront supprimés. " +
        "Le pilote PawnIO et .NET 10 restent installés (d'autres logiciels peuvent s'en servir).") $Danger $null 'Désinstaller' -WithCancel
    $script:mainAction = 'uninstall'
}
$ui.BtnGoUninstall.Add_Click({ Show-UninstallConfirm })
$ui.BtnSecond.Add_Click({ if ($Uninstall) { $win.Close() } else { $ui.Subtitle.Text = "Températures et utilisation de ton PC, toujours à l'écran."; Set-LogoColor $Accent; Set-Mark $null; Show-Page 'PageOptions' } })

# travail en arrière-plan (runspace) + suivi par minuterie : la fenêtre reste fluide
function Start-Work([bool]$uninstallMode) {
    $script:busy = $true
    $script:workUninstall = $uninstallMode
    $Opt.AutoStart = [bool]$ui.OptAuto.IsChecked; $Opt.Desktop = [bool]$ui.OptDesk.IsChecked; $Opt.Launch = [bool]$ui.OptLaunch.IsChecked
    $color = $(if ($uninstallMode) { $Danger } else { $Accent })
    Set-LogoColor $color; Set-Mark $null
    $ui.Fill.Background = New-Object Windows.Media.SolidColorBrush $color
    $ui.LogoArc.Data = [Windows.Media.Geometry]::Parse('M 12.17,45.83 A 23.8,23.8 0 0 1 9.5,15.35')     # arc court qui tourne
    $spin = New-Object Windows.Media.Animation.DoubleAnimation 0, 360, ([TimeSpan]::FromSeconds(1.1))
    $spin.RepeatBehavior = [Windows.Media.Animation.RepeatBehavior]::Forever
    $ui.LogoRot.BeginAnimation([Windows.Media.RotateTransform]::AngleProperty, $spin)
    $ui.Subtitle.Text = $(if ($uninstallMode) { 'Désinstallation en cours…' } else { 'Installation en cours…' })
    $ui.Hint.Visibility = $(if ($uninstallMode -or -not $needRuntime) { 'Collapsed' } else { 'Visible' })
    Show-Page 'PageProgress'

    $script:sync = [hashtable]::Synchronized(@{ P = 0.0; S = 'Préparation'; Done = $false; Err = $null })
    $rs = [runspacefactory]::CreateRunspace(); $rs.ApartmentState = 'STA'; $rs.Open()
    $rs.SessionStateProxy.SetVariable('sync', $script:sync)
    $rs.SessionStateProxy.SetVariable('Opt', $Opt)
    $rs.SessionStateProxy.SetVariable('Version', $Version)
    $rs.SessionStateProxy.SetVariable('Mode', $(if ($uninstallMode) { 'u' } else { 'i' }))
    $ps = [powershell]::Create(); $ps.Runspace = $rs
    [void]$ps.AddScript($Engine.ToString()).AddScript({
        $Report = { param($p, $s) $sync.P = $p; $sync.S = $s }
        try { if ($Mode -eq 'u') { Uninstall-Temperator $Opt $Report } else { Install-Temperator $Opt $Report } }
        catch { $sync.Err = $_.Exception.Message }
        $sync.Done = $true
    }.ToString())
    $script:job = $ps.BeginInvoke()

    $script:timer = New-Object Windows.Threading.DispatcherTimer
    $script:timer.Interval = [TimeSpan]::FromMilliseconds(120)
    $script:timer.Add_Tick({
        $ui.Status.Text = $script:sync.S
        $ui.FillScale.BeginAnimation([Windows.Media.ScaleTransform]::ScaleXProperty, (New-Object Windows.Media.Animation.DoubleAnimation ([double]$script:sync.P), ([TimeSpan]::FromMilliseconds(250))))
        if ($script:sync.Done) {
            $script:timer.Stop(); $script:busy = $false
            if ($script:sync.Err) {
                Show-Message 'Un problème est survenu.' $script:sync.Err $Danger 'M 22,22 L 36,36 M 36,22 L 22,36' 'Fermer'
            } elseif ($script:workUninstall) {
                Show-Message 'Désinstallé.' 'Temperator a été retiré de ce PC.' $Muted 'M 19,30 L 26,37 L 40,22' 'Terminer'
            } else {
                $txt = $(if ($Opt.Launch) { "Il est lancé : regarde en haut à droite de l'écran. " } else { 'Lance-le depuis le menu Démarrer. ' }) +
                       "Glisse-le pour le déplacer, double-clic pour le plan du PC, clic droit pour les réglages. Son icône près de l'horloge le masque ou le réaffiche."
                Show-Message "C'est installé !" $txt $Accent 'M 19,30 L 26,37 L 40,22' 'Terminer'
            }
            $script:mainAction = 'close'
            if ($AutoTest) {
                Add-Content (Join-Path $env:TEMP 'temperator-setup.log') ("{0} interface : {1} | {2}" -f (Get-Date -Format HH:mm:ss), $ui.Subtitle.Text, $ui.Message.Text) -Encoding UTF8
                $close = New-Object Windows.Threading.DispatcherTimer; $close.Interval = [TimeSpan]::FromSeconds(4)
                $close.Add_Tick({ $this.Stop(); $win.Close() }); $close.Start()
            }
        }
    })
    $script:timer.Start()
}

$script:mainAction = 'close'
$ui.BtnInstall.Add_Click({ Start-Work $false })
$ui.BtnMain.Add_Click({ if ($script:mainAction -eq 'uninstall') { Start-Work $true } else { $win.Close() } })

# ------------------------------------------------------------------ aperçu (développement)
if ($Preview) {
    New-Item -ItemType Directory -Force $Preview | Out-Null
    function Snap($file) {
        $ui.Card.Measure((New-Object Windows.Size $win.Width, $win.Height))
        $ui.Card.Arrange((New-Object Windows.Rect 0, 0, $win.Width, $win.Height))
        $ui.Card.UpdateLayout()
        foreach ($p in 'PageOptions', 'PageProgress', 'PageMessage') { $ui[$p].BeginAnimation([Windows.UIElement]::OpacityProperty, $null); $ui[$p].Opacity = 1 }
        $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap ([int]$win.Width), ([int]$win.Height), 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
        $rtb.Render($ui.Card)
        $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
        $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb))
        $fs = [IO.File]::Create((Join-Path $Preview $file)); $enc.Save($fs); $fs.Close()
    }
    Snap '1-options.png'
    Apply-Missing $true $true; Snap '1b-options-pc-sans-dotnet-ni-pawnio.png'; Apply-Missing $false $false
    $ui.Subtitle.Text = 'Installation en cours…'; $ui.LogoArc.Data = [Windows.Media.Geometry]::Parse('M 12.17,45.83 A 23.8,23.8 0 0 1 9.5,15.35')
    $ui.Status.Text = 'Installation de .NET 10 (Microsoft, environ 57 Mo)'; $ui.FillScale.ScaleX = 0.35; Show-Page 'PageProgress'; Snap '2-progression.png'
    Show-Message "C'est installé !" "Il est lancé : regarde en haut à droite de l'écran. Glisse-le pour le déplacer, double-clic pour le plan du PC, clic droit pour les réglages. Son icône près de l'horloge le masque ou le réaffiche." $Accent 'M 19,30 L 26,37 L 40,22' 'Terminer'; Snap '3-termine.png'
    Show-UninstallConfirm; Snap '4-desinstaller.png'
    Show-Message 'Un problème est survenu.' "Temperator est installé, mais le pilote PawnIO (obligatoire) n'a pas pu s'installer. Vérifie ta connexion internet et relance l'installeur, ou installe PawnIO depuis pawnio.eu." $Danger 'M 22,22 L 36,36 M 36,22 L 22,36' 'Fermer'; Snap '5-erreur.png'
    exit
}

if ($Uninstall) { Show-UninstallConfirm }
$win.Add_ContentRendered({ [void]$win.Activate() })   # au premier plan, même lancé depuis Temperator-Setup.exe
if ($AutoTest) {
    $win.Add_ContentRendered({
        $go = New-Object Windows.Threading.DispatcherTimer; $go.Interval = [TimeSpan]::FromSeconds(3)
        $go.Add_Tick({ $this.Stop(); if ($Uninstall) { Start-Work $true } else { Start-Work $false } }); $go.Start()
    })
}
[void]$win.ShowDialog()
