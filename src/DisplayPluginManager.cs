using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SnakeTail
{
    /// <summary>
    /// Information about a loaded display plugin.
    /// </summary>
    internal sealed class LoadedDisplayPlugin
    {
        public LoadedDisplayPlugin(string directoryName, string pluginDirectoryPath, string displayName, ILogDisplayPlugin instance, bool isAvailable, string unavailableReason)
        {
            DirectoryName = directoryName;
            PluginDirectoryPath = pluginDirectoryPath;
            DisplayName = displayName;
            Instance = instance;
            IsAvailable = isAvailable;
            UnavailableReason = unavailableReason ?? string.Empty;
        }

        public string DirectoryName { get; }
        public string PluginDirectoryPath { get; }
        public string DisplayName { get; }
        public ILogDisplayPlugin Instance { get; }
        public bool IsAvailable { get; }
        public string UnavailableReason { get; }
    }

    /// <summary>
    /// Display plugin manager: scans, loads, instantiates and initializes plugins.
    /// </summary>
    internal sealed class DisplayPluginManager
    {
        private readonly string _configPath;
        private readonly List<LoadedDisplayPlugin> _availablePlugins = new List<LoadedDisplayPlugin>();
        private readonly Dictionary<string, LoadedDisplayPlugin> _pluginsByName = new Dictionary<string, LoadedDisplayPlugin>(StringComparer.CurrentCultureIgnoreCase);

        public DisplayPluginManager(string configPath)
        {
            _configPath = configPath ?? string.Empty;
        }

        public IReadOnlyList<LoadedDisplayPlugin> AvailablePlugins => _availablePlugins;

        /// <summary>
        /// Rescans the plugin directory and builds the list of available plugins.
        /// </summary>
        public void Reload(string currentLogFilePath)
        {
            _availablePlugins.Clear();
            _pluginsByName.Clear();

            string pluginRootPath = GetPluginRootPath();
            if (!Directory.Exists(pluginRootPath))
                return;

            foreach (string pluginDirectory in Directory.GetDirectories(pluginRootPath))
            {
                string pluginDirectoryName = Path.GetFileName(pluginDirectory);
                if (string.IsNullOrEmpty(pluginDirectoryName))
                    continue;

                if (TryLoadPluginDirectory(pluginDirectory, pluginDirectoryName, currentLogFilePath, out LoadedDisplayPlugin loadedPlugin))
                {
                    if (_pluginsByName.ContainsKey(loadedPlugin.DisplayName))
                    {
                        AppLog.AppendDaily(AppLog.LevelWarn, string.Format("Duplicate display plugin name ignored: Name={0}, Directory={1}", loadedPlugin.DisplayName, loadedPlugin.PluginDirectoryPath));
                        continue;
                    }

                    _availablePlugins.Add(loadedPlugin);
                    _pluginsByName[loadedPlugin.DisplayName] = loadedPlugin;
                }
            }
        }

        /// <summary>
        /// Finds a plugin instance by display name.
        /// </summary>
        public LoadedDisplayPlugin FindByDisplayName(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
                return null;

            _pluginsByName.TryGetValue(displayName, out LoadedDisplayPlugin plugin);
            return plugin;
        }

        /// <summary>
        /// Resolves the plugin root directory (config/plugins).
        /// </summary>
        internal string GetPluginRootPath()
        {
            string basePath = !string.IsNullOrEmpty(_configPath) ? _configPath : Directory.GetCurrentDirectory();
            string configRootPath = Path.Combine(basePath, "config");
            return Path.Combine(configRootPath, "plugins");
        }

        /// <summary>
        /// Scans a single plugin directory and loads the first valid plugin by the rules.
        /// </summary>
        internal bool TryLoadPluginDirectory(string pluginDirectoryPath, string pluginDirectoryName, string currentLogFilePath, out LoadedDisplayPlugin loadedPlugin)
        {
            loadedPlugin = null;

            string[] dllFiles;
            try
            {
                dllFiles = Directory.GetFiles(pluginDirectoryPath, "*.dll", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                AppLog.AppendDaily(AppLog.LevelErr, string.Format("Failed to scan plugin directory: Directory={0}, Error={1}: {2}", pluginDirectoryPath, ex.GetType().FullName, ex.Message));
                return false;
            }

            if (dllFiles.Length == 0)
            {
                AppLog.AppendDaily(AppLog.LevelWarn, string.Format("No DLL found in plugin directory: Directory={0}", pluginDirectoryPath));
                return false;
            }

            foreach (string dllPath in dllFiles)
            {
                if (TryLoadPluginFromAssembly(dllPath, pluginDirectoryPath, pluginDirectoryName, currentLogFilePath, out loadedPlugin))
                    return true;
            }

            AppLog.AppendDaily(AppLog.LevelWarn, string.Format("No ILogDisplayPlugin implementation found in plugin directory: Directory={0}", pluginDirectoryPath));
            return false;
        }

        /// <summary>
        /// Loads and initializes a plugin from a DLL.
        /// </summary>
        private bool TryLoadPluginFromAssembly(string assemblyPath, string pluginDirectoryPath, string pluginDirectoryName, string currentLogFilePath, out LoadedDisplayPlugin loadedPlugin)
        {
            loadedPlugin = null;

            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(assemblyPath);
            }
            catch (Exception ex)
            {
                AppLog.AppendDaily(AppLog.LevelErr, string.Format("Failed to load plugin DLL: Dll={0}, Error={1}: {2}", assemblyPath, ex.GetType().FullName, ex.Message));
                return false;
            }

            if (!TryCreatePluginInstance(assembly, out ILogDisplayPlugin plugin, out string createError))
            {
                if (!string.IsNullOrEmpty(createError))
                {
                    AppLog.AppendDaily(AppLog.LevelErr, string.Format("Failed to create plugin instance: Dll={0}, Error={1}", assemblyPath, createError));
                }
                return false;
            }

            string configRootPath = Path.Combine(!string.IsNullOrEmpty(_configPath) ? _configPath : Directory.GetCurrentDirectory(), "config");
            string hostVersion = Assembly.GetExecutingAssembly().GetName().Version != null
                ? Assembly.GetExecutingAssembly().GetName().Version.ToString()
                : string.Empty;
            PluginContext context = new PluginContext(pluginDirectoryPath, configRootPath, currentLogFilePath, hostVersion);
            try
            {
                plugin.Initialize(context);
            }
            catch (FileNotFoundException ex)
            {
                string pluginName = GetPluginDisplayName(plugin, pluginDirectoryName);
                string unavailableReason = BuildMissingConfigReason(ex);
                AppLog.AppendDaily(AppLog.LevelWarn, string.Format("Plugin is missing a config file and cannot be enabled yet: Dll={0}, Plugin={1}, Reason={2}", assemblyPath, pluginName, unavailableReason));
                loadedPlugin = new LoadedDisplayPlugin(pluginDirectoryName, pluginDirectoryPath, pluginName, null, false, unavailableReason);
                return true;
            }
            catch (DirectoryNotFoundException ex)
            {
                string pluginName = GetPluginDisplayName(plugin, pluginDirectoryName);
                string unavailableReason = string.Format("Missing config directory: {0}", ex.Message);
                AppLog.AppendDaily(AppLog.LevelWarn, string.Format("Plugin is missing a config directory and cannot be enabled yet: Dll={0}, Plugin={1}, Reason={2}", assemblyPath, pluginName, unavailableReason));
                loadedPlugin = new LoadedDisplayPlugin(pluginDirectoryName, pluginDirectoryPath, pluginName, null, false, unavailableReason);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.AppendDaily(AppLog.LevelErr, string.Format("Plugin initialization failed: Dll={0}, Plugin={1}, Error={2}: {3}", assemblyPath, plugin.GetType().FullName, ex.GetType().FullName, ex.Message));
                return false;
            }

            string pluginDisplayName = GetPluginDisplayName(plugin, pluginDirectoryName);
            loadedPlugin = new LoadedDisplayPlugin(pluginDirectoryName, pluginDirectoryPath, pluginDisplayName, plugin, true, string.Empty);
            return true;
        }

        // Resolves the plugin display name in one place, avoiding duplicate empty-value branches.
        private static string GetPluginDisplayName(ILogDisplayPlugin plugin, string pluginDirectoryName)
        {
            string pluginName = plugin != null ? plugin.Name : string.Empty;
            if (string.IsNullOrWhiteSpace(pluginName))
                pluginName = pluginDirectoryName;
            return pluginName;
        }

        // Builds the exact missing-config-file reason, shared by UI notices and logs.
        private static string BuildMissingConfigReason(FileNotFoundException ex)
        {
            string missingPath = ex.FileName;
            if (!string.IsNullOrWhiteSpace(missingPath))
                return string.Format("{0}: {1}", ex.Message, missingPath);

            return ex.Message;
        }

        /// <summary>
        /// Finds the first instantiable plugin type in the assembly.
        /// </summary>
        internal static bool TryCreatePluginInstance(Assembly assembly, out ILogDisplayPlugin plugin, out string error)
        {
            plugin = null;
            error = null;

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                IEnumerable<string> loaderMessages = ex.LoaderExceptions != null
                    ? ex.LoaderExceptions.Where(x => x != null).Select(x => x.GetType().FullName + ": " + x.Message)
                    : Enumerable.Empty<string>();
                error = "Reflection type load failed: " + string.Join(" | ", loaderMessages);
                return false;
            }
            catch (Exception ex)
            {
                error = ex.GetType().FullName + ": " + ex.Message;
                return false;
            }

            Type pluginType = types.FirstOrDefault(t =>
                t != null &&
                typeof(ILogDisplayPlugin).IsAssignableFrom(t) &&
                t.IsClass &&
                !t.IsAbstract &&
                t.GetConstructor(Type.EmptyTypes) != null);
            if (pluginType == null)
                return false;

            try
            {
                plugin = (ILogDisplayPlugin)Activator.CreateInstance(pluginType);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().FullName + ": " + ex.Message;
                return false;
            }
        }
    }
}
