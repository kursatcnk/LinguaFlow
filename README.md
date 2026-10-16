# LinguaFlow

LinguaFlow, Windows'ta oynatılan İngilizce sesi yakalayıp Türkçe canlı altyazıya dönüştüren native bir WPF uygulamasıdır. Varsayılan **Yerel Ücretsiz** modunda konuşma tanıma ve çeviri tamamen bilgisayarda çalışır; Azure anahtarı, abonelik veya sürekli internet gerekmez.

## Özellikler

- NAudio 3 ve WASAPI loopback ile seçilen Windows ses çıkışını yakalar.
- Sesi 16 kHz, 16-bit, mono PCM'e dönüştürür; 16/24/32-bit PCM ve float girişleri destekler.
- Whisper `base.en` ile İngilizce konuşmayı yerel olarak metne çevirir.
- Mozilla Bergamot'un resmi `en-tr` modeliyle metni cihazda Türkçeye çevirir.
- Ham ses veya metin yerel modda bilgisayardan çıkmaz ve diske kaydedilmez.
- Always-on-top, taşınabilir altyazı overlay'i ve sınırlı geçmiş sunar.
- Model indirmelerini yarıda kalırsa sürdürür ve SHA-256 ile doğrular.
- İsteğe bağlı Azure Speech modu ile partial/final bulut sonuçlarını destekler.
- Açıkça etiketlenmiş Demo modu, gerçek servis kullanmadan arayüzü sınar.

## Gereksinimler

- Windows 10/11 x64
- Derlemek için [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Hızlı başlangıç

1. `.\publish.ps1` ile uygulamayı derleyin ve `artifacts\publish\win-x64\LinguaFlow.Desktop.exe` dosyasını çalıştırın.
2. Provider olarak `Yerel Ücretsiz — cihazda` seçin.
3. İlk kullanımda `Modelleri İndir` düğmesine basın. Yaklaşık 170 MB dosya `D:\LinguaFlowModels` altına iner.
4. Ses çıkışını seçin, İngilizce bir video/ses açın ve `Başlat` düğmesine basın.

Modeller indikten sonra yerel mod çevrimdışı çalışır. Sonuçlar cümle sonlarında görünür; Whisper akıştan anlamlı bir konuşma parçası tamamlandığında Bergamot çevirisi başlar.

## Dosya konumları

- Yerel modeller: `D:\LinguaFlowModels`
- .NET/NuGet önbelleği: `D:\CodexCache\LinguaFlow`
- Ayarlar: yayın klasörünün yanındaki `data\settings.json`

`LINGUAFLOW_MODEL_DIR` ve `LINGUAFLOW_DATA_DIR` ortam değişkenleriyle D:/E: üzerindeki farklı klasörler seçilebilir. Uygulama model klasörü olarak C: sürücüsünü kabul etmez. Ayrıntılar: [docs/CONFIGURATION.md](docs/CONFIGURATION.md).

## Azure (isteğe bağlı)

Azure modu kullanılacaksa anahtar ve bölge yalnızca bellekte/environment variable üzerinden okunur; anahtar `settings.json` dosyasına yazılmaz:

```powershell
$env:LINGUAFLOW_AZURE_SPEECH_KEY = "anahtar"
$env:LINGUAFLOW_AZURE_SPEECH_REGION = "westeurope"
```

## Build, test ve publish

PowerShell'de:

```powershell
.\build.ps1
.\publish.ps1
```

Scriptler .NET CLI ve NuGet önbelleklerini zorunlu olarak D: üzerinde tutar. `build.ps1` Release derlemesini ve tüm testleri; `publish.ps1` self-contained Windows x64 yayınını üretir.

Doğrudan komut kullanmanız gerekirse:

```powershell
$env:DOTNET_CLI_HOME = "D:\CodexCache\LinguaFlow\dotnet-home"
$env:NUGET_PACKAGES = "D:\CodexCache\LinguaFlow\nuget-packages"
dotnet build LinguaFlow.slnx -c Release
dotnet test LinguaFlow.slnx -c Release --no-restore
```

## Test kapsamı

- Partial → final transcript yaşam döngüsü ve tekrar filtreleme
- Sınırlı transcript geçmişi
- Float stereo 48 kHz → PCM16 mono 16 kHz dönüşümü
- Parça sınırında audio frame hizalama ve geçersiz bit derinliği
- Ses etkinliği, sessizlikle cümle sonlandırma ve kısa gürültü filtreleme
- Fake capture → normalize → provider → transcript entegrasyon hattı
- Manuel model smoke testi: Whisper native yükleme ve iki gerçek Bergamot en→tr çevirisi

## Bilinen sınırlar

- Yerel mod şu anda final cümle sonuçları üretir; kelime kelime partial altyazı vermez.
- Process-specific capture, otomatik cihaz değişimi/reconnect, tray, TXT/SRT export ve TTS sonraki fazdadır.
- Konuşma segmentasyonu enerji eşiği kullanır; çok düşük ses düzeylerinde kaynak ses artırılmalıdır.
- Gerçek loopback kabul testi, aktif bir Windows playback cihazı ve oynatılan İngilizce ses gerektirir.

## Bileşenler ve lisanslar

- [Whisper.net](https://github.com/sandrohanea/whisper.net) — MIT
- [whisper.cpp modelleri](https://huggingface.co/ggerganov/whisper.cpp) — model kaynağı
- [BergamotTranslatorSharp](https://github.com/Freeesia/BergamotTranslatorSharp) — MPL-2.0
- [Mozilla Translations model registry](https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/db/models.json) — resmi `en-tr` model kaynağı
- [NAudio](https://github.com/naudio/NAudio) — MIT

Bu bağımlılıkların model/binary dosyaları Git deposuna eklenmez; kullanıcı isteğiyle D: sürücüsüne indirilir.
