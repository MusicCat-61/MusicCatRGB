@echo off
echo KeyGlowMaster by MusicCat161 paketleniyor...
echo.

:: Release Modunda, Klasor olarak derle (Tek dosya modunda DLL sorunlari yasanmamasi icin)
dotnet publish MusicCat.CLVKM.UI\MusicCat.CLVKM.UI.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o ./Build_Output

echo.
echo ========================================================
echo ISLEM TAMAMLANDI!
echo Dosyalariniz "Build_Output" klasorunde hazir.
echo.
echo KGMaster.exe uygulamasini istediginiz yere tasiyabilirsiniz.
echo Ilk calistirmada otomatik olarak baslangica (Registry) eklenecektir.
echo ========================================================
pause
