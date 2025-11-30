# OrderPeek (Çiçeksepeti)

Çiçeksepeti satıcılarının siparişlerinde, **alt sipariş no (orderItemNo)** ile ürüne ait **kişiselleştirme metinlerini** hızlıca görüp **tek tıkla kopyalamak** için geliştirilmiş küçük bir WinForms yardımcı araçtır.

> Bu proje **resmi değildir**. Çiçeksepeti ile bir ortaklık/bağlılık/onarım ilişkisi yoktur.  
> Kişisel kullanım ve operasyonel hız için geliştirilmiştir, ürün olarak satılmaz.

## Ne işe yarar?
- orderItemNo gir → o alt siparişin **kişiselleştirme alanlarını** listeler
- Listeden satıra tıkla → sadece **müşteri girişi** panoya kopyalanır (metne dokunmaz)
- İstersen ↻ ile belirlediğin tarih aralığındaki siparişlerden cache oluşturur (rate-limit uyumlu)

## Özellikler
- ✅ **Alan / Müşteri girişi** şeklinde listeleme
- ✅ Tek tıkla **değer kopyalama**
- ✅ Cache mantığı (tekrar tekrar API’ye yüklenmez)
- ✅ Prefetch aralığı ayarı:
  - `PrefetchPastDays` (kaç gün geriye)
  - `PrefetchFutureDays` (kaç gün ileriye)
- ✅ Küçük pencere, sağ-alt köşe, always-on-top kullanım

## Kurulum / Çalıştırma
1. Uygulamayı aç
2. ⚙ **Ayarlar** → `x-api-key` gir
3. Gerekirse Sandbox seçimini yap
4. ↻ ile cache’i çek
5. orderItemNo yaz → Enter
6. Satıra tıkla → değer panoya kopyalanır ✅

## Güvenlik
- API key repoda tutulmaz.
- Build/publish çıktıları repoya eklenmez (`bin/`, `obj/`, `publish/`, `dist/`).

## Geliştirme
- Visual Studio 2022
- .NET (WinForms)
