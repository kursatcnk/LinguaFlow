# LinguaFlow yapılandırması

## Yerel Whisper modu (varsayılan)

API anahtarı gerektirmez. İlk çalıştırmada:

1. Motor olarak **Yerel Whisper (ücretsiz)** seçin.
2. **Modelleri indir** düğmesine basın. İndirilenler:
   - Whisper modeli: `ggml-large-v3-turbo-q5_0.bin` (~575 MB) ya da `ggml-small-q5_1.bin` (~190 MB)
   - Silero VAD: `ggml-silero-v5.1.2.bin` (~1 MB)
   - Çeviri dilinize göre cihazdaki çeviri paketleri (ör. `en-tr`, ~20 MB)
3. Ses çıkışını seçip **Başlat**'a basın.

Varsayılan model dizini `D:\LinguaFlowModels`. Başka bir D:/E: klasörü için uygulamayı açmadan önce:

```powershell
$env:LINGUAFLOW_MODEL_DIR = "D:\AI\LinguaFlow"
```

Model yolu C: olursa uygulama yazmayı reddeder. Yarıda kalan indirmeler `.partial` dosyasından devam eder; Whisper ve VAD modelleri Hugging Face'teki resmi SHA-256 ile, çeviri modelleri Mozilla kayıt defterindeki SHA-256 ile doğrulanır.

### GPU

Whisper önce Vulkan (NVIDIA/AMD/Intel, ekstra kurulum yok), olmazsa CPU çalışma zamanıyla yüklenir. Hangisinin kullanıldığı durum çubuğunda görünür (ör. `GPU (Vulkan)`). İlk açılışta GPU çekirdeklerinin derlenmesi ~1–10 sn sürebilir; uygulama bunu açılışta arka planda yapar.

### Çeviri motorları

| Mod | Kesin cümle | Anlık parça | İnternet |
|---|---|---|---|
| Çevrimiçi + cihaz yedeği | Google ücretsiz uç → cihaz | Cihaz → (≥1,2 sn aralıkla) Google | Yalnızca metin |
| Yalnızca cihazda | Mozilla Bergamot | Mozilla Bergamot | Paket indirme hariç yok |

Cihazdaki çeviri `dil → en` ve `en → dil` modelleriyle çalışır; örneğin Almanca → Türkçe için `de-en` ve `en-tr` paketleri kullanılır. Paketler `D:\LinguaFlowModels\translation\<çift>\` altına iner. Kayıt defteri (`models.json`) haftada bir yenilenir.

Çevrimiçi servis HTTP 429 döndürürse 3 dakika, başka bir hata olursa 15 saniye boyunca cihazdaki çeviri kullanılır.

Kesin cümleler çevrimiçi serviste, son 20 sn içindeki önceki cümleyle (aynı dilde, en fazla ~280 karakter) tek istekte ve satır sonuyla ayrılarak gönderilir; yalnızca yeni cümlenin çevirisi gösterilir. Servis satırları birleştirirse cümle tek başına yeniden çevrilir. Çeviriden önce konuşmaya özgü dolgu sözcükleri (`um`, `uh`, `äh`, `ııı`, `hmm`…; yalnızca o dilde dolgu olanlar) ve `wh- what` gibi yarım sözcükler atılır, tamamı büyük harfle yazılmış cümleler küçültülür. Ekranda Whisper'ın özgün metni görünmeye devam eder.

### Ana konuşmacı filtresi

**Ayarlar → Çevrilecek ses → Yalnızca ana konuşmacı** (varsayılan) açıkken bir Whisper segmenti şu durumlarda çevrilmez:

| Neden | Ölçüt |
|---|---|
| Şarkı | Baskın (segmentin en yüksek sesinin −10 dB içindeki) seslendirilmiş karelerin ≥ %50'si 260 ms'den uzun tutulan notalarda; ya da ≥ %30 ve Whisper güveni < 0,75. Whisper'ın `♪` ile bitirdiği sözler. |
| Düşük güven | Whisper'ın ortalama token olasılığı < 0,35 |
| Arka plan | Ana konuşmacının ses seviyesinden 15 dB'den fazla kısık. Ana konuşmacı seviyesi kesinleşen cümlelerden öğrenilir; ses kısılırsa saniyede 1,5 dB gevşer. |

Filtre bir şeyi atladığında durum çubuğunda (en fazla 6 sn'de bir) "Arka planda müzik/şarkı var" benzeri bir not görünür. Ayrıca Whisper'a giden ses 90 Hz altında süzülür; bu bas ve davulun konuşmayı maskelemesini azaltır ve filtre kapalıyken de uygulanır.

## Azure modu (isteğe bağlı, ücretli)

1. Azure Portal'da bir Azure AI Speech kaynağı oluşturun.
2. **Keys and Endpoint** bölümünden key ve region değerini alın.
3. Değerleri environment variable olarak ayarlayın veya arayüze girin.

```powershell
$env:LINGUAFLOW_AZURE_SPEECH_KEY = "kendi-anahtarınız"
$env:LINGUAFLOW_AZURE_SPEECH_REGION = "kaynak-bölgeniz"
```

Anahtar ayar dosyasına veya Git deposuna yazılmaz. Azure modunda ses buluta gönderilir. Azure sürekli tanımada tek bir konuşma dili ister; "Otomatik algıla" seçiliyse `en-US` kullanılır.

## Ayarlar ve veri

`LINGUAFLOW_DATA_DIR` tanımlı değilse ayarlar EXE yanındaki `data\settings.json` dosyasına yazılır:

```powershell
$env:LINGUAFLOW_DATA_DIR = "D:\LinguaFlowData"
```

Kaydedilenler: ses cihazı, motor, konuşma/çeviri dili, model, gecikme modu, altyazı konumu/genişliği/yazı boyutu/saydamlığı. Ham ses hiçbir modda diske yazılmaz.

## Sorun giderme

- **Model yüklenemedi: Native Library not found:** Yayın klasöründe `runtimes\` alt klasörü EXE'nin yanında olmalıdır; yalnızca EXE'yi kopyalamayın.
- **Hiç altyazı çıkmıyor:** Doğru ses çıkışının seçildiğinden ve sesin o cihazdan çaldığından emin olun (kulaklık/hoparlör). Durum çubuğu "Dinleniyor" göstermelidir.
- **Çeviri yerine özgün metin görünüyor:** Çevrimiçi servis sınırlanmış ve gerekli cihaz paketi henüz inmemiş olabilir; birkaç saniye içinde paket iner ve çeviri döner.
- **Gecikme yüksek / CPU görünüyor:** GPU sürücüsünü güncelleyin; GPU yoksa **Small** modelini seçin.
- **Yanlış dil algılanıyor:** Konuşma dilini elle seçin.
- **Konuşma bazen çevrilmiyor / şarkı yine çevriliyor:** Ana konuşmacı filtresi sezgiseldir. Konuşma atlanıyorsa *Çevrilecek ses → Duyulan her şey* seçin; durum çubuğundaki notlar filtrenin ne zaman devreye girdiğini gösterir.
- **401/403 (Azure):** Key ve region aynı Speech kaynağına ait olmayabilir.
- **Cihaz çıkarıldı:** Listeyi yenileyin, yeni cihazı seçin ve tekrar başlatın.
