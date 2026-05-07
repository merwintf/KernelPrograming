public static string BuildWebViewHtml(string content)
{
    if (content == null)
        content = "";

    bool isFullHtml =
        content.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0 ||
        content.IndexOf("<body", StringComparison.OrdinalIgnoreCase) >= 0;

    string style =
        "<style>" +
        "html, body {" +
        "background: white !important;" +
        "color: black !important;" +
        "margin: 0;" +
        "padding: 12px;" +
        "font-family: Segoe UI, sans-serif;" +
        "}" +
        "</style>";

    if (isFullHtml)
    {
        return style + content;
    }

    return
        "<!DOCTYPE html>" +
        "<html>" +
        "<head>" +
        "<meta charset=\"utf-8\">" +
        style +
        "</head>" +
        "<body>" +
        content +
        "</body>" +
        "</html>";
}
