using Playnite;

namespace Playnite;

public class LocalizedString : Markup.LocStringMarkup
{
    public LocalizedString() : base(NintendoLibrary.NintendoLibraryPlugin.Id)
    {
    }

    public LocalizedString(string stringId) : base(NintendoLibrary.NintendoLibraryPlugin.Id, stringId)
    {
    }
}

public static partial class Loc
{
    public static IPlayniteApi Api = null!;

    public static string GetString(string stringId)
    {
        return Api.GetLocalizedString(stringId);
    }

    public static string GetString(string stringId, params (string name, object value)[] args)
    {
        return Api.GetLocalizedString(stringId, args);
    }

    public static bool IsStringId(string id)
    {
        return Api.IsLocalizedStringId(id);
    }
}
