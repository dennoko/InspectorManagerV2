using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace DennokoWorks.InspectorManagerV2.UnityAdapter
{
    /// <summary>
    /// Resolves project-isolated user-local storage directory outside Assets/ (Section 10).
    /// Never generates asset files inside the Unity project directory.
    /// </summary>
    public static class UnityProjectPaths
    {
        public static string GetProjectId()
        {
            string normalized = Path.GetFullPath(UnityEngine.Application.dataPath ?? string.Empty)
                .Replace('\\', '/')
                .TrimEnd('/')
                .ToLowerInvariant();

            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var sb = new StringBuilder(32);
                for (int i = 0; i < 16 && i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
        }

        public static string GetProjectStorageDirectory(string projectId)
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir))
            {
                baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            if (string.IsNullOrEmpty(baseDir))
            {
                baseDir = Path.GetTempPath();
            }

            return Path.Combine(baseDir, "DennokoWorks", "InspectorManagerV2", string.IsNullOrEmpty(projectId) ? "default" : projectId);
        }
    }
}
