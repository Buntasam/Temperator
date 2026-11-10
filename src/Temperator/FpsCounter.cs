using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Temperator;

/// <summary>
/// Images par seconde d'un jeu : compte les images que le jeu envoie à l'écran (événements « Present » de DirectX,
/// lus par ETW comme le fait PresentMon). Couvre DirectX 9 à 12 ; les jeux Vulkan ou OpenGL purs ne sont pas vus.
/// Ne tourne que pendant un plein écran : rien n'est écouté le reste du temps. Nécessite les droits administrateur.
/// </summary>
sealed class FpsCounter : IDisposable
{
    static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");   // Microsoft-Windows-DXGI
    static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");   // Microsoft-Windows-D3D9
    const int DxgiPresent = 42, DxgiPresentOverlay = 55, D3D9Present = 1;
    const string SessionName = "Temperator-FPS";

    readonly ConcurrentDictionary<int, int> counts = new();
    readonly Stopwatch clock = new();
    TraceEventSession? session;
    float? smoothed;
    bool failed;

    public bool Running => session != null;
    /// <summary>Pourquoi le compteur n'a pas pu démarrer (diagnostic).</summary>
    public string? LastError { get; private set; }

    public void Start()
    {
        if (session != null || failed) return;
        try
        {
            // une session restée ouverte après un plantage porte le même nom : elle est remplacée
            session = new TraceEventSession(SessionName) { StopOnDispose = true };
            session.EnableProvider(Dxgi, TraceEventLevel.Informational);
            session.EnableProvider(D3D9, TraceEventLevel.Informational);
            session.Source.AllEvents += Count;
            var s = session;
            new Thread(() =>
            {
                try { s.Source.Process(); }
                catch (Exception ex) { App.Log(ex); }
            }) { IsBackground = true, Name = "FPS" }.Start();
            counts.Clear();
            smoothed = null;
            clock.Restart();
        }
        catch (Exception ex)
        {
            failed = true;   // pas de droits, ETW indisponible… : on n'insiste pas
            LastError = ex.Message;
            App.Log(ex);
            Stop();
        }
    }

    void Count(TraceEvent e)
    {
        int id = (int)e.ID;
        if (e.ProviderGuid == Dxgi ? id is DxgiPresent or DxgiPresentOverlay : e.ProviderGuid == D3D9 && id == D3D9Present)
            counts.AddOrUpdate(e.ProcessID, 1, (_, n) => n + 1);
    }

    /// <summary>Images par seconde du processus depuis le dernier appel (lissées), ou null si rien n'est mesuré.</summary>
    public float? Sample(int pid)
    {
        if (session == null) return null;
        double seconds = clock.Elapsed.TotalSeconds;
        if (seconds < 0.25) return smoothed;
        clock.Restart();
        counts.TryRemove(pid, out int frames);
        counts.Clear();
        if (frames == 0) return smoothed = null;
        float fps = (float)(frames / seconds);
        smoothed = smoothed is float previous ? previous * 0.4f + fps * 0.6f : fps;
        return smoothed;
    }

    /// <summary>Calcul pur (testable) : images comptées sur une durée → images par seconde.</summary>
    public static float Rate(int frames, double seconds) => seconds <= 0 ? 0 : (float)(frames / seconds);

    public void Stop()
    {
        try { session?.Dispose(); } catch { }
        session = null;
        smoothed = null;
    }

    public void Dispose() => Stop();
}
