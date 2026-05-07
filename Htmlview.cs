public static string BuildWebViewHtml(string content)
{
    var isFullHtml =
        content.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
        content.Contains("<body", StringComparison.OrdinalIgnoreCase);

    if (isFullHtml)
    {
        return $"""
        <style>
          html, body {{
            background: white !important;
            color: black !important;
          }}
        </style>
        {content}
        """;
    }

    return $"""
    <!DOCTYPE html>
    <html>
    <head>
      <meta charset="utf-8">
      <style>
        html, body {{
          background: white !important;
          color: black !important;
          margin: 0;
          padding: 12px;
          font-family: Segoe UI, sans-serif;
        }}
      </style>
    </head>
    <body>
      {content}
    </body>
    </html>
    """;
}
