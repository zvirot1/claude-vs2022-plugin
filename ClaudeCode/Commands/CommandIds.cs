using System;

namespace ClaudeCode
{
    /// <summary>
    /// Command IDs and GUIDs matching the VSCT file.
    /// </summary>
    public static class CommandIds
    {
        public static readonly Guid CommandSetGuid = new("D2E3F4A5-B6C7-8901-DEF0-234567890123");

        public const int OpenClaude = 0x0100;
        public const int NewSession = 0x0110;
        public const int SendSelection = 0x0200;
        public const int ExplainCode = 0x0210;
        public const int ReviewCode = 0x0220;
        public const int RefactorCode = 0x0230;
        public const int AnalyzeFile = 0x0240;
        public const int FocusToggle = 0x0300;
        public const int InsertAtMention = 0x0400;
    }
}
