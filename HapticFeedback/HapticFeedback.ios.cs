namespace HapticFeedback;

public class HapticFeedback : IHapticFeedback
{
    public void Click()
    {
        Impact(UIImpactFeedbackStyle.Light);
    }

    public void LongPress()
    {
        Impact(UIImpactFeedbackStyle.Medium);
    }

    private static void Impact(UIImpactFeedbackStyle style)
    {
        using var generator = CreateGenerator(style);
        generator.Prepare();
        generator.ImpactOccurred();
    }

    private static UIImpactFeedbackGenerator CreateGenerator(UIImpactFeedbackStyle style)
    {
        if (OperatingSystem.IsIOSVersionAtLeast(17, 5))
        {
            // Since iOS 17.5 generators must be associated with a view; use the key window.
            var window = UIApplication.SharedApplication.ConnectedScenes
                .OfType<UIWindowScene>()
                .SelectMany(scene => scene.Windows)
                .FirstOrDefault(w => w.IsKeyWindow);
            if (window is not null)
            {
                return UIImpactFeedbackGenerator.GetFeedbackGenerator(style, window);
            }
        }

#pragma warning disable CA1422 // pre-17.5 fallback, also covers the no-key-window edge case
        return new UIImpactFeedbackGenerator(style);
#pragma warning restore CA1422
    }
}
