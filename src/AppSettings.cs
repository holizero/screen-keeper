using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace ScreenKeeper
{
    sealed class AppSettings
    {
        public int IntervalSeconds = 30;
        public uint Modifiers = GlobalShortcut.DefaultModifiers;
        public Keys ShortcutKey = GlobalShortcut.DefaultKey;

        public static AppSettings Load(string path)
        {
            AppSettings result = new AppSettings();
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 16384) return result;
                string[] lines = File.ReadAllLines(path); int interval, key; uint modifiers;
                if (lines.Length > 0 && int.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out interval) && interval >= 5 && interval <= 3600)
                    result.IntervalSeconds = interval;
                if (lines.Length >= 3 && uint.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out modifiers) &&
                    int.TryParse(lines[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out key) && GlobalShortcut.Valid(modifiers, (Keys)key))
                { result.Modifiers = modifiers; result.ShortcutKey = (Keys)key; }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            return result;
        }
        public bool Save(string path)
        {
            if (IntervalSeconds < 5 || IntervalSeconds > 3600 || !GlobalShortcut.Valid(Modifiers, ShortcutKey)) return false;
            string temporary = null;
            try
            {
                string fullPath = Path.GetFullPath(path), directory = Path.GetDirectoryName(fullPath);
                Directory.CreateDirectory(directory);
                temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
                string content = IntervalSeconds.ToString(CultureInfo.InvariantCulture) + "\n" +
                    Modifiers.ToString(CultureInfo.InvariantCulture) + "\n" + ((int)ShortcutKey).ToString(CultureInfo.InvariantCulture) + "\n";
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(content);
                    stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
                // 같은 디렉터리의 임시 파일을 교체하므로 실패해도 기존 설정을 잘라 쓰지 않습니다.
                if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
                else File.Move(temporary, fullPath);
                temporary = null; return true;
            }
            catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
            finally
            {
                if (temporary != null)
                    try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
