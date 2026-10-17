#region License statement
/* SnakeTail is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3 of the License.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml;
using System.Xml.Serialization;

using SnakeTail;

namespace SnakeTail.Storage
{
    /// <summary>
    /// Unified xSnakeTail.db storage: recently opened files, default session, etc., decoupled from MainForm.
    /// </summary>
    public sealed class SnakeTailStorage : IDisposable
    {
        private const string KeyDefaultSession = "DefaultSession";
        private const string KeyThemeMode = "ThemeMode";

        private readonly string _dbPath;
        private object _connection;
        private readonly object _lockObject = new object();
        private Assembly _sqliteAssembly;
        private Type _sqliteConnectionType;
        private Type _sqliteCommandType;
        private Type _sqliteDataReaderType;
        private bool _isAvailable;

        public SnakeTailStorage(string dbPath = null)
        {
            if (string.IsNullOrEmpty(dbPath))
            {
                // Use %LOCALAPPDATA%\SnakeTail\xSnakeTail.db so the exe directory isn't used
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                _dbPath = Path.Combine(localAppData, "SnakeTail", "xSnakeTail.db");
            }
            else
            {
                _dbPath = dbPath;
            }

            if (LoadSqliteAssembly())
                InitializeDatabase();
        }

        public bool IsAvailable => _isAvailable;

        #region Recently opened files (RecentFiles)

        public List<string> GetRecentFiles(int maxCount)
        {
            var files = new List<string>();
            if (!_isAvailable || _connection == null) return files;

            try
            {
                lock (_lockObject)
                {
                    string sql = "SELECT FilePath FROM RecentFiles ORDER BY LastAccessed DESC LIMIT @MaxCount";
                    object reader = ExecuteReader(sql, "@MaxCount", maxCount);
                    if (reader == null) return files;

                    try
                    {
                        var readMethod = _sqliteDataReaderType.GetMethod("Read");
                        var getStringMethod = _sqliteDataReaderType.GetMethod("GetString", new Type[] { typeof(int) });
                        while ((bool)readMethod.Invoke(reader, null))
                        {
                            string filePath = (string)getStringMethod.Invoke(reader, new object[] { 0 });
                            if (File.Exists(filePath))
                                files.Add(filePath);
                        }
                    }
                    finally
                    {
                        _sqliteDataReaderType.GetMethod("Close")?.Invoke(reader, null);
                    }
                }
            }
            catch { }

            return files;
        }

        public void AddFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !_isAvailable || _connection == null) return;
            try
            {
                lock (_lockObject)
                {
                    string fullPath = Path.GetFullPath(filePath);
                    string sql = @"INSERT OR REPLACE INTO RecentFiles (FilePath, LastAccessed, AccessCount)
                        VALUES (@FilePath, @LastAccessed, COALESCE((SELECT AccessCount FROM RecentFiles WHERE FilePath = @FilePath), 0) + 1)";
                    ExecuteNonQuery(sql, "@FilePath", fullPath, "@LastAccessed", DateTime.Now);
                }
            }
            catch { }
        }

        public void RemoveFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !_isAvailable || _connection == null) return;
            try
            {
                lock (_lockObject)
                {
                    ExecuteNonQuery("DELETE FROM RecentFiles WHERE FilePath = @FilePath", "@FilePath", Path.GetFullPath(filePath));
                }
            }
            catch { }
        }

        public void RenameFile(string oldFilePath, string newFilePath)
        {
            if (string.IsNullOrEmpty(oldFilePath) || string.IsNullOrEmpty(newFilePath) || !_isAvailable || _connection == null) return;
            try
            {
                lock (_lockObject)
                {
                    string sql = "UPDATE RecentFiles SET FilePath = @NewFilePath, LastAccessed = @LastAccessed WHERE FilePath = @OldFilePath";
                    ExecuteNonQuery(sql, "@OldFilePath", Path.GetFullPath(oldFilePath), "@NewFilePath", Path.GetFullPath(newFilePath), "@LastAccessed", DateTime.Now);
                }
            }
            catch { }
        }

        public void ClearAllRecentFiles()
        {
            if (!_isAvailable || _connection == null) return;
            try { lock (_lockObject) { ExecuteNonQuery("DELETE FROM RecentFiles"); } }
            catch { }
        }

        public void CleanupNonExistentFiles()
        {
            if (!_isAvailable || _connection == null || _sqliteDataReaderType == null) return;
            try
            {
                lock (_lockObject)
                {
                    var toRemove = new List<string>();
                    object reader = ExecuteReader("SELECT FilePath FROM RecentFiles");
                    if (reader != null)
                    {
                        try
                        {
                            var readMethod = _sqliteDataReaderType.GetMethod("Read");
                            var getStringMethod = _sqliteDataReaderType.GetMethod("GetString", new Type[] { typeof(int) });
                            while (readMethod != null && getStringMethod != null && (bool)readMethod.Invoke(reader, null))
                            {
                                string filePath = (string)getStringMethod.Invoke(reader, new object[] { 0 });
                                if (!File.Exists(filePath)) toRemove.Add(filePath);
                            }
                        }
                        finally { _sqliteDataReaderType.GetMethod("Close")?.Invoke(reader, null); }
                    }
                    foreach (string filePath in toRemove)
                        RemoveFile(filePath);
                }
            }
            catch { }
        }

        #endregion

        #region Default session (DefaultSession)

        /// <summary>
        /// Saves the default session to the db (serialization is done inside this class)
        /// </summary>
        public void SaveDefaultSession(TailConfig config)
        {
            if (config == null || !_isAvailable) return;
            string xml = SerializeTailConfig(config);
            if (!string.IsNullOrEmpty(xml))
                SetSetting(KeyDefaultSession, xml);
        }

        /// <summary>
        /// Loads the default session from the db and deserializes it into a TailConfig
        /// </summary>
        public TailConfig LoadDefaultSession()
        {
            if (!_isAvailable) return null;
            string xml = GetSetting(KeyDefaultSession);
            return DeserializeTailConfig(xml);
        }

        private static string SerializeTailConfig(TailConfig config)
        {
            if (config == null) return null;
            try
            {
                var serializer = new XmlSerializer(typeof(TailConfig));
                using (var sw = new StringWriter())
                using (var writer = new XmlTextWriter(sw) { Formatting = Formatting.Indented })
                {
                    var xmlns = new XmlSerializerNamespaces();
                    xmlns.Add("", "");
                    serializer.Serialize(writer, config, xmlns);
                    return sw.ToString();
                }
            }
            catch { return null; }
        }

        private static TailConfig DeserializeTailConfig(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            try
            {
                var serializer = new XmlSerializer(typeof(TailConfig));
                using (var sr = new StringReader(xml))
                using (var reader = new XmlTextReader(sr))
                    return serializer.Deserialize(reader) as TailConfig;
            }
            catch { return null; }
        }

        #endregion

        #region Theme (ThemeMode)

        public void SaveThemeMode(ThemeMode mode)
        {
            SetSetting(KeyThemeMode, mode.ToString());
        }

        /// <summary>
        /// Loads the saved theme mode; Light when nothing has been saved
        /// </summary>
        public ThemeMode LoadThemeMode()
        {
            return Theme.ParseMode(GetSetting(KeyThemeMode));
        }

        #endregion

        #region Internal: SQLite and AppSettings

        private string GetSetting(string key)
        {
            if (string.IsNullOrEmpty(key) || !_isAvailable || _connection == null) return null;
            try
            {
                lock (_lockObject)
                {
                    object reader = ExecuteReader("SELECT Value FROM AppSettings WHERE Key = @Key", "@Key", key);
                    if (reader == null) return null;
                    try
                    {
                        var readMethod = _sqliteDataReaderType.GetMethod("Read");
                        var getStringMethod = _sqliteDataReaderType.GetMethod("GetString", new Type[] { typeof(int) });
                        if (readMethod != null && getStringMethod != null && (bool)readMethod.Invoke(reader, null))
                            return (string)getStringMethod.Invoke(reader, new object[] { 0 });
                    }
                    finally { _sqliteDataReaderType.GetMethod("Close")?.Invoke(reader, null); }
                }
            }
            catch { }
            return null;
        }

        private void SetSetting(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || !_isAvailable || _connection == null) return;
            try
            {
                lock (_lockObject)
                    ExecuteNonQuery("INSERT OR REPLACE INTO AppSettings (Key, Value) VALUES (@Key, @Value)", "@Key", key ?? "", "@Value", value ?? "");
            }
            catch { }
        }

        private bool LoadSqliteAssembly()
        {
            try
            {
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string[] paths = {
                    Path.Combine(exeDir, "System.Data.SQLite.dll"),
                    Path.Combine(Path.Combine(exeDir, "libs"), "System.Data.SQLite.dll"),
                    "System.Data.SQLite.dll"
                };
                foreach (string dllPath in paths)
                {
                    try
                    {
                        if (File.Exists(dllPath))
                        {
                            _sqliteAssembly = Assembly.LoadFrom(dllPath);
                            _sqliteConnectionType = _sqliteAssembly.GetType("System.Data.SQLite.SQLiteConnection");
                            _sqliteCommandType = _sqliteAssembly.GetType("System.Data.SQLite.SQLiteCommand");
                            _sqliteDataReaderType = _sqliteAssembly.GetType("System.Data.SQLite.SQLiteDataReader");
                            if (_sqliteConnectionType != null && _sqliteCommandType != null && _sqliteDataReaderType != null)
                            {
                                _isAvailable = true;
                                return true;
                            }
                        }
                    }
                    catch { continue; }
                }
                try
                {
                    _sqliteAssembly = Assembly.Load("System.Data.SQLite, Version=1.0.0.0, Culture=neutral, PublicKeyToken=db937bc2d44ff139");
                    _sqliteConnectionType = _sqliteAssembly.GetType("System.Data.SQLite.SQLiteConnection");
                    _sqliteCommandType = _sqliteAssembly.GetType("System.Data.SQLite.SQLiteCommand");
                    _sqliteDataReaderType = _sqliteAssembly.GetType("System.Data.SQLite.SQLiteDataReader");
                    if (_sqliteConnectionType != null && _sqliteCommandType != null && _sqliteDataReaderType != null)
                    {
                        _isAvailable = true;
                        return true;
                    }
                }
                catch { }
            }
            catch { }
            _isAvailable = false;
            return false;
        }

        private void InitializeDatabase()
        {
            if (!_isAvailable) return;
            lock (_lockObject)
            {
                try
                {
                    string directory = Path.GetDirectoryName(_dbPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                        Directory.CreateDirectory(directory);

                    string connectionString = string.Format("Data Source={0};Version=3;", _dbPath);
                    _connection = Activator.CreateInstance(_sqliteConnectionType, new object[] { connectionString });
                    _sqliteConnectionType.GetMethod("Open").Invoke(_connection, null);

                    bool isNew = !File.Exists(_dbPath);
                    if (isNew)
                        CreateTables();
                    else
                        ExecuteNonQuery("CREATE TABLE IF NOT EXISTS AppSettings (Key TEXT PRIMARY KEY, Value TEXT);");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(string.Format("Could not initialize SQLite: {0}\nPath: {1}", ex.Message, _dbPath), ex);
                }
            }
        }

        private void CreateTables()
        {
            string sql = @"
                CREATE TABLE IF NOT EXISTS RecentFiles (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FilePath TEXT NOT NULL UNIQUE,
                    LastAccessed DATETIME NOT NULL,
                    AccessCount INTEGER DEFAULT 1
                );
                CREATE INDEX IF NOT EXISTS idx_LastAccessed ON RecentFiles(LastAccessed DESC);
                CREATE TABLE IF NOT EXISTS AppSettings (Key TEXT PRIMARY KEY, Value TEXT);
            ";
            ExecuteNonQuery(sql);
        }

        private void ExecuteNonQuery(string sql, params object[] parameters)
        {
            if (!_isAvailable || _connection == null || _sqliteCommandType == null) return;
            object command = null;
            try
            {
                command = Activator.CreateInstance(_sqliteCommandType, new object[] { sql, _connection });
                if (command == null) return;
                if (parameters != null && parameters.Length > 0)
                {
                    var paramCollection = (command as System.Data.IDbCommand)?.Parameters;
                    var addWithValue = paramCollection?.GetType().GetMethod("AddWithValue");
                    if (addWithValue != null)
                        for (int i = 0; i < parameters.Length; i += 2)
                            addWithValue.Invoke(paramCollection, new object[] { parameters[i], parameters[i + 1] });
                }
                _sqliteCommandType.GetMethod("ExecuteNonQuery", Type.EmptyTypes)?.Invoke(command, null);
            }
            catch { }
            finally
            {
                if (command != null)
                    try { _sqliteCommandType.GetMethod("Dispose")?.Invoke(command, null); } catch { }
            }
        }

        private object ExecuteReader(string sql, params object[] parameters)
        {
            if (!_isAvailable || _connection == null || _sqliteCommandType == null) return null;
            try
            {
                object command = Activator.CreateInstance(_sqliteCommandType, new object[] { sql, _connection });
                if (command == null) return null;
                if (parameters != null && parameters.Length > 0)
                {
                    var paramCollection = (command as System.Data.IDbCommand)?.Parameters;
                    var addWithValue = paramCollection?.GetType().GetMethod("AddWithValue");
                    if (addWithValue != null)
                        for (int i = 0; i < parameters.Length; i += 2)
                            addWithValue.Invoke(paramCollection, new object[] { parameters[i], parameters[i + 1] });
                }
                return _sqliteCommandType.GetMethod("ExecuteReader", Type.EmptyTypes)?.Invoke(command, null);
            }
            catch { }
            return null;
        }

        #endregion

        public void Dispose()
        {
            if (_connection == null) return;
            try
            {
                if (_sqliteConnectionType != null)
                    lock (_lockObject)
                    {
                        try { _sqliteConnectionType.GetMethod("Close")?.Invoke(_connection, null); } catch { }
                        try { _sqliteConnectionType.GetMethod("Dispose")?.Invoke(_connection, null); } catch { }
                    }
            }
            catch { }
            finally { _connection = null; }
        }
    }
}
