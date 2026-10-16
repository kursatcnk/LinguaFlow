# LinguaFlow yapılandırması

## Yerel ücretsiz mod

Yerel mod varsayılandır ve API anahtarı istemez. İlk çalıştırmada:

1. Provider olarak **Yerel Ücretsiz — cihazda** seçin.
2. **Modelleri İndir** düğmesine basın.
3. Yaklaşık 170 MB modelin indirilip doğrulanmasını bekleyin.
4. Playback cihazını seçip **Başlat** düğmesine basın.

Varsayılan model dizini `D:\LinguaFlowModels` konumudur. Başka bir D:/E: klasörü için uygulamayı açmadan önce:

```powershell
$env:LINGUAFLOW_MODEL_DIR = "D:\AI\LinguaFlow"
```

Model yolu C: olursa uygulama yazmayı reddeder. İndirme sırasında bağlantı koparsa `.partial` dosyası korunur ve sonraki deneme kaldığı yerden sürer. Tamamlanan dosyalar SHA-256 ile doğrulanır.

Yerel motorlar:

- İngilizce konuşma tanıma: Whisper `base.en`
- İngilizce→Türkçe çeviri: Mozilla Bergamot resmi `en-tr` modeli

## Azure modu (isteğe bağlı)

1. Azure Portal'da bir Azure AI Speech kaynağı oluşturun.
2. **Keys and Endpoint** bölümünden key ve region değerini alın.
3. Değerleri yalnızca kendi makinenizde environment variable olarak ayarlayın veya uygulama arayüzüne girin.

```powershell
$env:LINGUAFLOW_AZURE_SPEECH_KEY = "kendi-anahtarınız"
$env:LINGUAFLOW_AZURE_SPEECH_REGION = "kaynak-bölgeniz"
```

Anahtar ayar dosyasına veya Git deposuna yazılmaz. Azure modunda ses buluta gönderilir; yerel modda gönderilmez.

## Dil kodları

- Kaynak: İngilizce (`en-US` / Whisper `en`)
- Hedef: Türkçe (`tr`)

## Ayarlar ve veri

`LINGUAFLOW_DATA_DIR` tanımlı değilse ayarlar EXE yanındaki `data\settings.json` dosyasına yazılır. Özel konum:

```powershell
$env:LINGUAFLOW_DATA_DIR = "D:\LinguaFlowData"
```

Azure anahtarı bu dosyaya kaydedilmez. Ham ses hiçbir modda diske yazılmaz.

## Sorun giderme

- **Modeller eksik:** Yerel provider'ı seçip **Modelleri İndir** düğmesine basın; `D:\LinguaFlowModels` altında boş alan ve yazma yetkisi olduğundan emin olun.
- **Ses yok:** Doğru playback endpoint'ini seçin ve İngilizce sesin bu cihazdan çıktığını doğrulayın. WASAPI loopback, cihaz tamamen sessizken veri üretmeyebilir.
- **Altyazı gecikiyor:** Yerel mod cümle sonundaki yaklaşık 650 ms sessizliği bekler. Çok uzun kesintisiz konuşmalar en fazla 12 saniyelik parçalara ayrılır.
- **Konuşma algılanmıyor:** Kaynak ses seviyesini yükseltin; enerji eşiğinin altındaki ses/gürültü filtrelenir.
- **401/403:** Azure key ve region aynı Speech kaynağına ait olmayabilir.
- **Dropped frame:** Sistem ses işleme hattına yetişememiştir; hata diagnostikte görünür.
- **Cihaz çıkarıldı:** Listeyi yenileyin, yeni cihazı seçin ve tekrar başlatın.
