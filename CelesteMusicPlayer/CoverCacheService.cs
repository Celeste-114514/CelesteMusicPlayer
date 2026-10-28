using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
namespace CelesteMusicPlayer;
/// <summary>Shared file-based cover cache. No window or image-control dependency.</summary>
internal static class CoverCacheService
{
    private static readonly SemaphoreSlim Gate = new(4, 4);
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> Flights = new(StringComparer.Ordinal);
    internal static CoverVariant ForWebSize(int size) => size <= 160 ? CoverVariant.Thumb : CoverVariant.Large;
    internal static Task<string?> GetAsync(byte[]? source, CoverVariant variant, string? root = null)
    {
        if(source is not {Length: > 0} || source.Length > 20_000_000)return Task.FromResult<string?>(null);
        root ??= CoverCacheLayout.GetRoot();
        var file=CoverCacheLayout.GetVariantFile(root,CoverCacheLayout.GetKey(source),variant);
        if(Exists(file))return Task.FromResult<string?>(file);
        var lazy=Flights.GetOrAdd(file,_=>new Lazy<Task<string?>>(()=>GenerateAsync(source,file,variant),LazyThreadSafetyMode.ExecutionAndPublication));
        return CompleteAsync(file,lazy);
    }
    private static bool Exists(string file) { try{return new FileInfo(file) is {Exists:true,Length:>0};}catch(IOException){return false;} }
    private static async Task<string?> CompleteAsync(string file,Lazy<Task<string?>> flight)
    {
        try{return await flight.Value.ConfigureAwait(false);}
        finally{Flights.TryRemove(new System.Collections.Generic.KeyValuePair<string,Lazy<Task<string?>>>(file,flight));}
    }
    private static async Task<string?> GenerateAsync(byte[] source,string file,CoverVariant variant)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        string temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            if(Exists(file))return file;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            bool ok=await Task.Run(()=>CoverThumbnailer.TryWriteVariant(source,temp,variant,out _)).ConfigureAwait(false);
            if(!ok)return null;
            File.Move(temp,file,true);return file;
        }
        finally{try{File.Delete(temp);}catch(IOException){} Gate.Release();}
    }
}
