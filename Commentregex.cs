var regex = new Regex(
    @"^CM(?<index>\d+):[ \t]*\r?\n(?<name>[^\r\n]*)\r?\n(?<content>.*?)(?=^CM\d+:|\z)",
    RegexOptions.Multiline | RegexOptions.Singleline
);
