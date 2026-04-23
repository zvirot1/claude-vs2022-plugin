using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ClaudeCode.UI
{
    /// <summary>
    /// Manages file attachments for Claude conversation context.
    /// Port of com.anthropic.claude.intellij.ui.AttachmentManager.
    /// </summary>
    public class AttachmentManager
    {
        public class FileAttachment
        {
            public string FilePath { get; }
            public string? RelativePath { get; set; }
            public int StartLine { get; set; }
            public int EndLine { get; set; }

            public FileAttachment(string filePath)
            {
                FilePath = filePath;
            }

            public string GetLabel()
            {
                var name = RelativePath ?? Path.GetFileName(FilePath);
                if (StartLine > 0 && EndLine > 0)
                    return $"{name} (L{StartLine}-{EndLine})";
                if (StartLine > 0)
                    return $"{name} (L{StartLine}+)";
                return name;
            }
        }

        public event Action? AttachmentsChanged;

        private readonly List<FileAttachment> _attachments = new List<FileAttachment>();
        private readonly string? _projectRoot;

        public AttachmentManager(string? projectRoot)
        {
            _projectRoot = projectRoot;
        }

        public void AttachFile(string filePath, int startLine = 0, int endLine = 0)
        {
            if (!File.Exists(filePath)) return;

            var attachment = new FileAttachment(filePath)
            {
                StartLine = startLine,
                EndLine = endLine
            };

            // Generate relative path from project root
            if (_projectRoot != null && filePath.StartsWith(_projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                attachment.RelativePath = filePath.Substring(_projectRoot.Length).TrimStart('\\', '/');
            }

            _attachments.Add(attachment);
            AttachmentsChanged?.Invoke();
        }

        public void AttachFileByPath(string relativePath)
        {
            if (_projectRoot == null) return;
            var fullPath = Path.Combine(_projectRoot, relativePath);
            if (File.Exists(fullPath))
                AttachFile(fullPath);
        }

        public void RemoveAttachment(int index)
        {
            if (index >= 0 && index < _attachments.Count)
            {
                _attachments.RemoveAt(index);
                AttachmentsChanged?.Invoke();
            }
        }

        public void ClearAttachments()
        {
            _attachments.Clear();
            AttachmentsChanged?.Invoke();
        }

        public IReadOnlyList<FileAttachment> GetAttachments() => _attachments.ToList();

        /// <summary>
        /// Builds file context XML for all attachments to include in the message.
        /// </summary>
        public string BuildFileContext()
        {
            if (_attachments.Count == 0) return "";

            var sb = new StringBuilder();
            foreach (var att in _attachments)
            {
                try
                {
                    var lines = File.ReadAllLines(att.FilePath);
                    var startIdx = att.StartLine > 0 ? att.StartLine - 1 : 0;
                    var endIdx = att.EndLine > 0 ? Math.Min(att.EndLine, lines.Length) : lines.Length;

                    var content = string.Join("\n", lines.Skip(startIdx).Take(endIdx - startIdx));
                    var name = att.RelativePath ?? Path.GetFileName(att.FilePath);

                    sb.AppendLine($"<file path=\"{name}\"");
                    if (att.StartLine > 0)
                        sb.Append($" startLine=\"{att.StartLine}\"");
                    if (att.EndLine > 0)
                        sb.Append($" endLine=\"{att.EndLine}\"");
                    sb.AppendLine(">");
                    sb.AppendLine(content);
                    sb.AppendLine("</file>");
                }
                catch { }
            }
            return sb.ToString();
        }
    }
}
