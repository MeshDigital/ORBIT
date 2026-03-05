using System;
using System.IO;
using System.Threading.Tasks;
using SLSKDONET.Models;
using TagLib;

namespace SLSKDONET.Services
{
    public interface ISafeWriteService
    {
        Task<bool> SafeWriteTagsAsync(string filePath, Action<TagLib.File> tagAction);
    }

    public class SafeWriteService : ISafeWriteService
    {
        private const double MIN_SIZE_RETENTION = 0.9; // Safety threshold

        public async Task<bool> SafeWriteTagsAsync(string filePath, Action<TagLib.File> tagAction)
        {
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                return false;

            string tmpPath = filePath + ".orbit_tmp";
            
            try
            {
                // 1. Create temporary copy
                await Task.Run(() => System.IO.File.Copy(filePath, tmpPath, true));

                // 2. Apply tags to the copy
                await Task.Run(() => 
                {
                    using (var tFile = TagLib.File.Create(tmpPath))
                    {
                        tagAction(tFile);
                        tFile.Save();
                    }
                });

                // 3. Validation: Size Check (DAW Safety)
                var originalInfo = new FileInfo(filePath);
                var tempInfo = new FileInfo(tmpPath);

                if (tempInfo.Length < (originalInfo.Length * MIN_SIZE_RETENTION))
                {
                    // Safety trip: File shrank too much, likely metadata wipe or corruption
                    System.IO.File.Delete(tmpPath);
                    return false;
                }

                // 4. Atomic Swap
                await Task.Run(() => 
                {
                    System.IO.File.Delete(filePath);
                    System.IO.File.Move(tmpPath, filePath);
                });

                return true;
            }
            catch (Exception)
            {
                // Cleanup on failure
                if (System.IO.File.Exists(tmpPath))
                    System.IO.File.Delete(tmpPath);
                
                return false;
            }
        }
    }
}
