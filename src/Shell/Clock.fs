module Shell.Clock

let utcNowIso () : string =
    System.DateTime.UtcNow.ToString("o")
