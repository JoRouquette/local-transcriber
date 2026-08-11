using System;
using System.Threading;

namespace LocalTranscriber.Gui;

/// <summary>
/// Garantit une seule instance de la GUI par session interactive. Repose sur un mutex nommé
/// (portée <c>Local\</c> = session utilisateur) : un second lancement détecte l'instance déjà
/// présente, la « réveille » via un <see cref="EventWaitHandle"/> nommé, puis se termine.
///
/// On utilise le constructeur <c>new Mutex(true, name, out createdNew)</c> et on ne se fie qu'à
/// <c>createdNew</c> : aucun <c>WaitOne</c>, donc jamais d'<see cref="AbandonedMutexException"/>
/// si une instance précédente a été tuée sans libérer le mutex.
/// </summary>
public static class SingleInstanceGuard
{
    private const string MutexName = @"Local\LocalTranscriber.Gui.SingleInstance";
    private const string ActivateEventName = @"Local\LocalTranscriber.Gui.Activate";

    private static Mutex? _mutex;
    private static EventWaitHandle? _activateEvent;
    private static Thread? _listener;
    private static volatile bool _listening;

    /// <summary>
    /// Tente de devenir l'instance unique. Retourne <c>true</c> si on est la première instance
    /// (l'appelant continue le démarrage) ; <c>false</c> si une instance tourne déjà — dans ce
    /// cas on l'a réveillée et l'appelant doit se terminer immédiatement.
    /// </summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            // Première instance : on prépare l'event d'activation (le listener démarre plus tard,
            // une fois la fenêtre créée, via StartActivationListener).
            _activateEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                ActivateEventName
            );
            return true;
        }

        // Une instance existe déjà : on la réveille si possible, puis on rend la main pour sortir.
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing))
            {
                using (existing)
                    existing.Set();
            }
        }
        catch
        { /* si le réveil échoue, on sort quand même : pas de seconde fenêtre */
        }
        return false;
    }

    /// <summary>
    /// Démarre l'écoute des demandes d'activation (2ᵉ lancement). <paramref name="onActivate"/>
    /// est appelé sur un thread de fond ; l'implémentation doit marshaler vers le thread UI.
    /// </summary>
    public static void StartActivationListener(Action onActivate)
    {
        if (_activateEvent is null || _listening)
            return;
        _listening = true;
        _listener = new Thread(() =>
        {
            while (_listening)
            {
                try
                {
                    if (_activateEvent.WaitOne(500))
                        onActivate();
                }
                catch
                { /* le listener ne doit jamais faire tomber l'appli */
                }
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstanceActivationListener",
        };
        _listener.Start();
    }

    /// <summary>Libère les ressources (mutex + event) à la fermeture de l'application.</summary>
    public static void Release()
    {
        _listening = false;
        try
        {
            _activateEvent?.Dispose();
        }
        catch
        { /* rien à faire */
        }
        try
        {
            _mutex?.Dispose();
        }
        catch
        { /* rien à faire */
        }
    }
}
