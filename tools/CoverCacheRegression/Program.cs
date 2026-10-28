using CelesteMusicPlayer;
using SkiaSharp;
var root=Path.Combine(Path.GetTempPath(),"CelesteCoverCacheRegression-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try{
 byte[] ImageBytes(SKEncodedImageFormat format,int width,int height){using var bitmap=new SKBitmap(width,height);using(var canvas=new SKCanvas(bitmap))canvas.Clear(SKColors.Orange);using var image=SKImage.FromBitmap(bitmap);using var data=image.Encode(format,90);return data.ToArray();}
 void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
 Check(CoverCacheService.ForWebSize(160)==CoverVariant.Thumb&&CoverCacheService.ForWebSize(480)==CoverVariant.Large&&CoverCacheService.ForWebSize(768)==CoverVariant.Large,"web-size-mapping");
 foreach(var format in new[]{SKEncodedImageFormat.Jpeg,SKEncodedImageFormat.Webp}){
  var source=ImageBytes(format,1200,600);var tasks=Enumerable.Range(0,20).Select(_=>CoverCacheService.GetAsync(source,CoverVariant.Large,root));var paths=await Task.WhenAll(tasks);Check(paths.All(p=>p==paths[0]&&p!=null),"concurrent-dedup-"+format);
  using var decoded=SKBitmap.Decode(paths[0]);Check(decoded.Width==768&&decoded.Height==384,"aspect-ratio-"+format);var stamp=File.GetLastWriteTimeUtc(paths[0]!);await CoverCacheService.GetAsync(source,CoverVariant.Large,root);Check(stamp==File.GetLastWriteTimeUtc(paths[0]!),"cache-hit-no-rewrite-"+format);
 }
 var small=await CoverCacheService.GetAsync(ImageBytes(SKEncodedImageFormat.Png,40,80),CoverVariant.Thumb,root);using(var bitmap=SKBitmap.Decode(small))Check(bitmap.Width==40&&bitmap.Height==80,"no-upscaling");
 Check(await CoverCacheService.GetAsync(new byte[]{1,2,3},CoverVariant.Thumb,root)==null,"invalid-image-fallback");Check(!Directory.EnumerateFiles(root,"*.tmp",SearchOption.AllDirectories).Any(),"no-temp-leaks");
}finally{Directory.Delete(root,true);}
