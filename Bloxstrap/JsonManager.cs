namespace Bloxstrap
{
    public class JsonManager<T> where T : class, new()
    {
        protected T _prop = new();

        public virtual T Prop
        {
            get => _prop;
            set => _prop = value;
        }

        /// <summary>
        /// The file hash when last retrieved from disk
        /// </summary>
        public string? LastFileHash { get; private set; }

        private string _savedSnapshot = JsonSerializer.Serialize(new T());
        public bool HasUnsavedChanges => JsonSerializer.Serialize(Prop) != _savedSnapshot;
        public bool LastSaveSucceeded { get; private set; }

        public bool Loaded { get; protected set; } = false;

        public virtual string ClassName { get; }

        public virtual string FileName => $"{ClassName}.json";

        public virtual string FileLocation => Path.Combine(Paths.Base, FileName);

        public bool IsSaved => File.Exists(FileLocation);

        public virtual string LOG_IDENT_CLASS => $"JsonManager<{ClassName}>";

        public JsonManager(string? className = null)
        {
            ClassName = string.IsNullOrEmpty(className) ? typeof(T).Name : className;
        }

        public virtual bool Load(bool alertFailure = true)
        {
            string LOG_IDENT = $"{LOG_IDENT_CLASS}::Load";

            App.Logger.WriteLine(LOG_IDENT, $"Loading from {FileLocation}...");

            try
            {
                if (File.Exists(FileLocation))
                {
                    string contents = File.ReadAllText(FileLocation);

                    LastFileHash = MD5Hash.FromString(contents);
                    T? settings = JsonSerializer.Deserialize<T>(contents);

                    if (settings is null)
                        throw new ArgumentNullException("Deserialization returned null");

                    _prop = settings;
                    _savedSnapshot = JsonSerializer.Serialize(settings);
                    Loaded = true;

                    App.Logger.WriteLine(LOG_IDENT, "Loaded successfully!");

                    return true;
                }
                else
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Could not find {FileLocation}.");
                    Loaded = true;

                    return false;
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Failed to load!");
                App.Logger.WriteException(LOG_IDENT, ex);

                // A temporary read/access failure is not a corrupt configuration.
                // Never replace settings with defaults when we could not read them.
                if (ex is IOException or UnauthorizedAccessException)
                    return false;

                if (alertFailure)
                {
                    string message = "";

                    if (ClassName == nameof(Settings))
                        message = Strings.JsonManager_SettingsLoadFailed;
                    else if (ClassName == nameof(FastFlagManager))
                        message = Strings.JsonManager_FastFlagsLoadFailed;

                    if (!String.IsNullOrEmpty(message))
                        Frontend.ShowMessageBox($"{message}\n\n{ex.Message}", System.Windows.MessageBoxImage.Warning);

                    try
                    {
                        // Create a backup of loaded file
                        File.Copy(FileLocation, FileLocation + ".bak", true);
                    }
                    catch (Exception copyEx)
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Failed to create backup file: {FileLocation}.bak");
                        App.Logger.WriteException(LOG_IDENT, copyEx);
                    }
                }

                Loaded = true;
                Save();

                return false;
            }
        }

        public virtual void Save()
        {
            string LOG_IDENT = $"{LOG_IDENT_CLASS}::Save";
            
            App.Logger.WriteLine(LOG_IDENT, $"Saving to {FileLocation}...");

            LastSaveSucceeded = false;
            string temporaryFile = FileLocation + "." + Guid.NewGuid().ToString("N") + ".tmp";

            using var writeLock = new Mutex(false, "Finstrap-Config-" + MD5Hash.FromString(Path.GetFullPath(FileLocation).ToUpperInvariant()));
            bool ownsLock = false;
            try
            {
                try { ownsLock = writeLock.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { ownsLock = true; }
                if (!ownsLock)
                    throw new IOException("Another Finstrap window is saving this file. Please retry.");
                if (Loaded && LastFileHash != (File.Exists(FileLocation) ? MD5Hash.FromFile(FileLocation) : null))
                    throw new IOException("This file was changed by another Finstrap window. Reopen settings before editing again to preserve those changes.");

                string contents = JsonSerializer.Serialize(Prop, new JsonSerializerOptions { WriteIndented = true });

                Directory.CreateDirectory(Path.GetDirectoryName(FileLocation)!);
                File.WriteAllText(temporaryFile, contents);
                if (ClassName == nameof(Settings) && File.Exists(FileLocation) && !File.Exists(FileLocation + ".pre-command-center.bak"))
                    File.Copy(FileLocation, FileLocation + ".pre-command-center.bak");
                File.Move(temporaryFile, FileLocation, overwrite: true);
                _savedSnapshot = JsonSerializer.Serialize(Prop);
                LastSaveSucceeded = true;

                LastFileHash = MD5Hash.FromString(contents);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                App.Logger.WriteLine(LOG_IDENT, "Failed to save");
                App.Logger.WriteException(LOG_IDENT, ex);

                string errorMessage = string.Format(Resources.Strings.Bootstrapper_JsonManagerSaveFailed, ClassName, ex.Message);
                Frontend.ShowMessageBox(errorMessage, System.Windows.MessageBoxImage.Warning);

                return;
            }

            finally
            {
                if (ownsLock)
                    writeLock.ReleaseMutex();
                try { File.Delete(temporaryFile); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            App.Logger.WriteLine(LOG_IDENT, "Save complete!");
        }

        public virtual void Delete()
        {
            string LOG_IDENT = $"{LOG_IDENT_CLASS}::Delete";

            try
            {
                if (File.Exists(FileLocation))
                {
                    File.Delete(FileLocation);

                    Loaded = false;
                    App.Logger.WriteLine(LOG_IDENT, "Delete complete!");
                }
                else
                {
                    App.Logger.WriteLine(LOG_IDENT, "File does not exist on disk");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                App.Logger.WriteLine(LOG_IDENT, "Failed to delete");
                App.Logger.WriteException(LOG_IDENT, ex);

                // should we notify?
            }
        }

        /// <summary>
        /// Is the file on disk different to the one deserialised during this session?
        /// </summary>
        public bool HasFileOnDiskChanged()
        {
            // check if a file has been created since launch
            if (string.IsNullOrEmpty(LastFileHash) && File.Exists(FileLocation))
                return true;

            return LastFileHash != MD5Hash.FromFile(FileLocation);
        }
    }

    /// <summary>
    /// <see cref="JsonManager{T}"/> that will automatically load in the JSON if it has not been already
    /// </summary>
    /// <typeparam name="T">Class</typeparam>
    public class LazyJsonManager<T> : JsonManager<T> where T : class, new()
    {
        public override T Prop
        {
            get
            {
                if (!Loaded)
                    Load();

                return _prop;
            }
            set
            {
                _prop = value;
                Loaded = true;
            }
        }

        public LazyJsonManager(string? className)
            : base(className)
        {
        }
    }
}
