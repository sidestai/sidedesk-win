using System;
using System.IO;
using System.Reflection;

namespace SideDesk
{
    public static class Paths
    {
        private const string SETTINGS = "settings.json";

        public static string CurrentDirectory
        {
            get
            {
                return Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory)!;
            }
        }

        private static string? _assemblyName = null;

        public static string AssemblyName
        {
            get
            {
                if (_assemblyName == null)
                {
                    _assemblyName = Assembly.GetExecutingAssembly().GetName().Name!;
                }

                return _assemblyName;
            }
        }

        private static string? _exeName = null;

        public static string ExeName
        {
            get
            {
                if (_exeName == null)
                {
                    _exeName = string.Format("{0}.exe", AssemblyName);
                }

                return _exeName;
            }
        }

        private static string? _settingsFile = null;

        public static string SettingsFile
        {
            get
            {
                if (_settingsFile == null)
                {
                    _settingsFile = Path.Combine(LocalApp, SETTINGS);
                }

                return _settingsFile;
            }
        }

        private static string? _localApp = null;

        public static string LocalApp
        {
            get
            {
                if (_localApp == null)
                {
                    _localApp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AssemblyName);
                }

                return _localApp;
            }
        }
    }
}
