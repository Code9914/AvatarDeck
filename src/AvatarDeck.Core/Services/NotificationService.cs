using AvatarDeck.Core.Abstractions;

namespace AvatarDeck.Core.Services;

/// <summary>Default in-process notification hub. The UI subscribes and renders toasts.</summary>
public sealed class NotificationService(TimeProvider time) : INotificationService
{
    public event EventHandler<Notification>? Raised;

    public void Notify(string message, NotificationLevel level = NotificationLevel.Info) =>
        Raised?.Invoke(this, new Notification(message, level, time.GetUtcNow()));
}
