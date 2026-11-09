using System;
using System.IO;

namespace Town.Core.Runtime
{
    /// <summary>单槽本地存档：先完整写临时文件，再原子替换；保留上一份备份。不自动覆盖坏档。</summary>
    public sealed class TownSaveStore
    {
        private readonly string _path;
        public TownSaveStore(string path) { _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path))); }
        public void Write(TownSave save)
        {
            byte[] bytes = TownSaveCodec.Encode(save);
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(_path)) File.Replace(temp, _path, _path + ".bak");
                else File.Move(temp, _path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public TownRuntime Read(int generation, out TownSave loadedSave, out bool usedBackup)
        {
            loadedSave = null;
            usedBackup = false;
            Exception first;
            try { return ReadOne(_path, generation, out loadedSave); }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is System.Runtime.Serialization.SerializationException || e is ArgumentException)
            { first = e; }
            try
            {
                var runtime = ReadOne(_path + ".bak", generation, out loadedSave);
                usedBackup = true;
                return runtime;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is System.Runtime.Serialization.SerializationException || e is ArgumentException)
            { throw new InvalidDataException("主存档和备份均不可用；当前局未修改。", first); }
        }

        private static TownRuntime ReadOne(string path, int generation, out TownSave loadedSave)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > TownSaveCodec.MaxBytes) throw new InvalidDataException("存档大小无效。");
                byte[] bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int count = stream.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0) throw new InvalidDataException("存档读取不完整。");
                    offset += count;
                }
                var save = TownSaveCodec.Decode(bytes);
                var runtime = TownSaveCodec.Restore(save, generation);
                loadedSave = save;
                return runtime;
            }
        }
    }
}
