namespace Playnite;

public static partial class Loc
{

    /// <summary>
    /// Failed to import games from {$libName}
    /// </summary>
    public static string library_import_error(object libName)
    {
        return GetString("library_import_error", ("libName", libName));
    }
    /// <summary>
    /// Connect account
    /// </summary>
    public static string settings_connect_account()
    {
        return GetString("settings_connect_account");
    }
    /// <summary>
    /// Authenticate
    /// </summary>
    public static string authenticate_label()
    {
        return GetString("authenticate_label");
    }
    /// <summary>
    /// Checking authentication status…
    /// </summary>
    public static string login_checking()
    {
        return GetString("login_checking");
    }
    /// <summary>
    /// User is authenticated
    /// </summary>
    public static string logged_in()
    {
        return GetString("logged_in");
    }
    /// <summary>
    /// Requires authentication
    /// </summary>
    public static string not_logged_in()
    {
        return GetString("not_logged_in");
    }
    /// <summary>
    /// Nintendo Library
    /// </summary>
    public static string nintendo_library_label()
    {
        return GetString("nintendo_library_label");
    }
    /// <summary>
    /// Exclude add-on-only entries (DLC)
    /// </summary>
    public static string nintendo_exclude_addon_only_label()
    {
        return GetString("nintendo_exclude_addon_only_label");
    }
    /// <summary>
    /// Nintendo settings could not be saved. Check that the Playnite user data folder is writable.
    /// </summary>
    public static string nintendo_settings_save_failed()
    {
        return GetString("nintendo_settings_save_failed");
    }
}

public static partial class LocId
{

    /// <summary>
    /// Failed to import games from {$libName}
    /// </summary>
    public const string library_import_error = "library_import_error";
    /// <summary>
    /// Connect account
    /// </summary>
    public const string settings_connect_account = "settings_connect_account";
    /// <summary>
    /// Authenticate
    /// </summary>
    public const string authenticate_label = "authenticate_label";
    /// <summary>
    /// Checking authentication status…
    /// </summary>
    public const string login_checking = "login_checking";
    /// <summary>
    /// User is authenticated
    /// </summary>
    public const string logged_in = "logged_in";
    /// <summary>
    /// Requires authentication
    /// </summary>
    public const string not_logged_in = "not_logged_in";
    /// <summary>
    /// Nintendo Library
    /// </summary>
    public const string nintendo_library_label = "nintendo_library_label";
    /// <summary>
    /// Exclude add-on-only entries (DLC)
    /// </summary>
    public const string nintendo_exclude_addon_only_label = "nintendo_exclude_addon_only_label";
    /// <summary>
    /// Nintendo settings could not be saved. Check that the Playnite user data folder is writable.
    /// </summary>
    public const string nintendo_settings_save_failed = "nintendo_settings_save_failed";
}
