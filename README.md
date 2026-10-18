# LinguaFlow

LinguaFlow, Windows'ta çalan her türlü konuşmayı (video, toplantı, yayın, oyun) duyduğu anda yazıya döker ve seçtiğiniz dile çevirir. Çeviri, hem ana pencerede akan bir döküm olarak hem de her zaman üstte duran bir altyazı şeridinde görünür. Ücretsizdir; API anahtarı ya da abonelik gerekmez.

## Nasıl çalışır?

```
Sistem sesi (WASAPI loopback)
   → 16 kHz mono
   → Silero VAD: konuşma var mı, konuşmacı sustu mu? (her 100 ms)
   → Whisper Large v3 Turbo, GPU üzerinde (Vulkan): kayan pencere, ~0,3 sn'de bir yeniden çözümleme
   → Cümle kesinleştirme: duraksama + noktalama + ardışık iki tahminde aynı kalma
   → Çeviri: Google (ücretsiz uç) → sınırlanırsa / internet yoksa cihazdaki Mozilla modelleri
   → Canlı döküm + altyazı şeridi
```

- **Anlık (partial) altyazı:** Konuşma sürerken cümle, siz dinlerken yazılır ve çevrilir; Whisper cümleyi düzelttikçe çeviri de güncellenir.
- **Cümle sonunu anlama:** Konuşmacı kısa bir duraksama yapıp cümle noktalama ile bitiyorsa (~250 ms) ya da belirgin şekilde durursa (~600 ms) cümle kesinleşir. Kesintisiz konuşmada, tamamlanmış ve iki ardışık tahminde değişmeyen cümleler beklemeden kesinleşir.
- **Ölçülen gecikme (RTX 3070):** Whisper çözümlemesi 220–400 ms, çeviri 60–150 ms (çevrimiçi) / 3–15 ms (cihazda). Kesin çeviri, cümle bittikten ortalama ~0,5 sn sonra ekranda.
- **Tüm diller:** Konuşma dili otomatik algılanır (Whisper'ın 99 dili). Çevrimiçi çeviri tüm dil çiftlerini, cihazdaki çeviri İngilizce üzerinden 59 dili destekler.
- **Gizlilik:** Ses asla bilgisayardan çıkmaz ve diske yazılmaz. Çevrimiçi modda yalnızca tanınan metin çeviri servisine gider; "Yalnızca cihazda" modunda hiçbir şey dışarı çıkmaz.

## Gereksinimler

- Windows 10/11 x64
- Önerilen: Vulkan destekli bir GPU (NVIDIA/AMD/Intel; ek sürücü/CUDA kurulumu gerekmez). GPU yoksa CPU'ya düşer; bu durumda **Small** modelini seçin.
- Derlemek için [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Hızlı başlangıç

1. `.\publish.ps1` ile yayını üretin ve `artifacts\publish\win-x64\LinguaFlow.Desktop.exe` dosyasını çalıştırın. (`runtimes\` klasörü EXE'nin yanında kalmalıdır.)
2. Ayarlar panelinde **Modelleri indir** düğmesine basın (Turbo için ~575 MB, tek seferlik).
3. Dinlenecek ses çıkışını, konuşma dilini (ya da *Otomatik algıla*) ve çeviri dilini seçin.
4. **Başlat**'a basın (ya da `Ctrl+Enter`) ve bir video/yayın açın.

Altyazı şeridini sürükleyerek taşıyabilir, kenarlarından genişletebilirsiniz; üzerine gelince yazı boyutu, özgün metin ve saydamlık düğmeleri çıkar.

## Ayarlar

| Ayar | Seçenekler |
|---|---|
| Motor | **Yerel Whisper** (önerilen, ücretsiz) · Azure Speech (anahtar gerekir) · Demo |
| Konuşma tanıma modeli | **Large v3 Turbo** — en doğru, GPU önerilir · **Small** — hafif |
| Çeviri | **Çevrimiçi + cihaz yedeği** — en iyi kalite · **Yalnızca cihazda** — internetsiz |
| Gecikme | **Anlık** — en düşük gecikme · **Dengeli** — beam search, biraz daha doğru |

Konuşma dilini biliyorsanız seçmeniz, kısa cümlelerde dil karışmasını önler ve tanımayı hızlandırır.

### Çeviri bütçesi

Ücretsiz çevrimiçi çeviri ucu yoğun istekte IP'yi birkaç dakika sınırlayabilir (HTTP 429). LinguaFlow bu yüzden:

- anlık parçaları mümkünse cihazdaki modelle çevirir, çevrimiçi servise en fazla ~1,2 sn'de bir gönderir;
- kesin cümleleri en iyi kalite için çevrimiçi servise gönderir;
- servis sınırlarsa 3 dakika boyunca tamamen cihazdaki çeviriye geçer, sonra kendiliğinden geri döner.

Cihazdaki çeviri için gereken dil paketleri (çift başına ~20–40 MB) ilk ihtiyaçta arka planda iner.

## Dosya konumları

- Modeller: `D:\LinguaFlowModels` (Whisper, VAD) ve `D:\LinguaFlowModels\translation\<çift>` (çeviri paketleri)
- .NET/NuGet önbelleği: `D:\CodexCache\LinguaFlow`
- Ayarlar: EXE'nin yanındaki `data\settings.json`

`LINGUAFLOW_MODEL_DIR` ve `LINGUAFLOW_DATA_DIR` ortam değişkenleriyle farklı klasörler seçilebilir. Uygulama model klasörü olarak C: sürücüsünü kabul etmez. Ayrıntılar: [docs/CONFIGURATION.md](docs/CONFIGURATION.md).

## Build, test ve publish

```powershell
.\build.ps1     # Release derleme + tüm testler
.\publish.ps1   # self-contained win-x64 yayını
```

Scriptler .NET CLI ve NuGet önbelleklerini D: üzerinde tutar.

## Test kapsamı

- Cümle kesinleştirme politikası: sessizlik, duraksama + noktalama, kararlı önek, zorunlu kesinleştirme
- Whisper halüsinasyon/etiket temizliği ve tekrar döngüsü bastırma
- Çeviri yanıtı ayrıştırma, İngilizce pivot rotası, model kayıt defterinden sürüm seçimi, dil kodu eşleme
- Partial → final transcript yaşam döngüsü, sonraki cümlenin partial'ının korunması, partial geri çekme
- Float stereo 48 kHz → PCM16 mono 16 kHz dönüşümü ve frame hizalama
- Fake capture → normalize → provider → transcript entegrasyon hattı

## Bilinen sınırlar

- Aynı anda birden fazla kişinin üst üste konuştuğu seslerde tanıma doğruluğu düşer; konuşmacı ayrımı yoktur.
- Çok gürültülü müzik altındaki konuşmada VAD bazı cümleleri kaçırabilir.
- Cihazdaki çeviri İngilizce üzerinden pivot yaptığı için İngilizce dışı → İngilizce dışı çiftlerde çevrimiçi çeviriden bir miktar daha düşük kalitededir.
- Azure modunda "Otomatik algıla" İngilizce (en-US) olarak kullanılır.

## Bileşenler ve lisanslar

- [Whisper.net](https://github.com/sandrohanea/whisper.net) — MIT; [whisper.cpp modelleri](https://huggingface.co/ggerganov/whisper.cpp), [Silero VAD (ggml)](https://huggingface.co/ggml-org/whisper-vad)
- [BergamotTranslatorSharp](https://github.com/Freeesia/BergamotTranslatorSharp) — MPL-2.0; [Mozilla Translations modelleri](https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/db/models.json)
- [NAudio](https://github.com/naudio/NAudio) — MIT
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MIT

Model dosyaları Git deposuna eklenmez; kullanıcı isteğiyle D: sürücüsüne indirilir ve SHA-256 ile doğrulanır.

## Lisans

LinguaFlow [MIT lisansı](LICENSE) ile dağıtılır. Yukarıdaki bağımlılıklar ve modeller kendi lisanslarına tabidir.
