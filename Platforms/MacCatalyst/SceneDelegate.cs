using Foundation;

namespace FamilyFeud
{
    // Needed for multi-window support (#61): Info.plist's UIApplicationSceneManifest
    // points UISceneDelegateClassName at this class so OpenWindow can create new scenes.
    [Register("SceneDelegate")]
    public class SceneDelegate : MauiUISceneDelegate
    {
    }
}
