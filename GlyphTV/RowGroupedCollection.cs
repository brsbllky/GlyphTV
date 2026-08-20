// ============================================================
// RowGroupedCollection.cs
// KALICI DÜZELTME (CPU sıçramaları — sekme/kategori geçişi ve scroll
// sırasında).
//
// KÖK SEBEP: VodContentGrid / SeriesContentGrid / ContentItemsGrid
// (MainWindow.axaml) düz bir ObservableCollection'a WrapPanel ItemsPanel'i
// ile bağlıydı. Avalonia'nın WrapPanel'i SANALLAŞTIRMA (virtualization)
// DESTEKLEMEZ — ekranda hiç görünmeyen kartlar bile tam bir görsel ağaç
// (Border+Grid+Image+TextBlock+Button'lar) olarak oluşturulup ölçülüp
// yerleştiriliyordu. Liste sayfalama ile (PAGE_SIZE) büyüdükçe her yeni
// ekleme, o ana kadar oluşturulmuş TÜM kartların yeniden layout edilmesini
// tetikliyor, bu da sekme geçişi/scroll sırasında gözle görülür CPU
// sıçramalarına (bazen %100'e) yol açıyordu.
//
// ÇÖZÜM: Dış ItemsControl'ü artık gerçek sanallaştırma yapan
// VirtualizingStackPanel'e bağlıyoruz (bkz. MainWindow.axaml). Ama
// VirtualizingStackPanel tek yönde (dikey) istifler — kartları yatayda
// da gruplu (grid) göstermeye devam edebilmek için, düz kart listesini
// önce sabit sayıda sütunluk (GRID_COLUMNS) "satır" (CardRow) gruplarına
// ayırıyoruz. Dış panel artık binlerce kart yerine sadece birkaç yüz
// SATIR görür ve sadece ekranda görünen satırları gerçekten oluşturur;
// her satırın içindeki 3-4 kart ise küçük, sanallaştırılmayan (ama zaten
// çok ucuz olan) yatay bir StackPanel ile render edilir.
//
// ÖNEMLİ: Bu sınıf, MainWindow.Navigation.cs / MainWindow.Player.cs /
// MainWindow.ContentActions.cs içindeki _displayContents/_displayVodContents/
// _displaySeriesCards gibi düz ObservableCollection'ları ve onları
// güncelleyen TÜM kodu (ReplaceCollection, LoadMoreItems, .Add(), .Clear())
// HİÇBİR ŞEKİLDE DEĞİŞTİRMEZ. RowGroupedCollection sadece o koleksiyonların
// CollectionChanged olaylarını dinleyip kendi "satır" görünümünü arka
// planda senkron tutar — var olan sayfalama/performans deseni (O(yeni
// sayfa) maliyetli .Add()) aynen korunur.
//
// Bu uygulamada bu 5 koleksiyonda GÖZLEMLENEN işlemler zaten sabit/dar bir
// kümedir: Reset (Clear), SONDAN Add (sayfalama), SONDAN RemoveAt
// (ReplaceCollection'ın boyut küçültme dalı) ve aynı indexte Replace
// (indexer set). Bu dört durum burada özel olarak (ucuz, O(1)/satır
// maliyetle) ele alınır; beklenmeyen bir durumla karşılaşılırsa güvenli
// bir yola (tam yeniden inşa) düşülür — asla yanlış/eksik veri göstermez,
// sadece o an biraz daha pahalı olur.
// ============================================================

using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace GlyphTV
{
    /// <summary>
    /// Bir "satır"daki kart grubu. Dış (sanallaştırılan) ItemsControl bu
    /// tipin bir listesini gösterir; her satırın içindeki kartlar kendi
    /// (küçük, sanallaştırılmayan) yatay ItemsControl'ü ile render edilir.
    /// </summary>
    public sealed class CardRow<T>
    {
        public ObservableCollection<T> Items { get; } = new();
    }

    /// <summary>
    /// Kaynak düz koleksiyonu (_displayVodContents vb.) izleyip aynı
    /// öğeleri sabit sütun sayısına göre satırlara gruplayan salt-UI
    /// katmanı adaptörü. İş mantığı tarafında (Navigation/Player/
    /// ContentActions) hiçbir değişiklik gerektirmez.
    /// </summary>
    public sealed class RowGroupedCollection<T> : ObservableCollection<CardRow<T>>, IDisposable
    {
        private readonly ObservableCollection<T> _source;
        private int _columns;
        private bool _disposed;

        public RowGroupedCollection(ObservableCollection<T> source, int columns)
        {
            _source = source;
            _columns = Math.Max(1, columns);
            _source.CollectionChanged += OnSourceChanged;
            RebuildAll();
        }

        /// <summary>
        /// Sütun sayısını değiştirir (ör. pencere tam ekrana geçip
        /// genişleyince). Değer gerçekten değiştiyse satırlar sıfırdan
        /// yeniden gruplanır — kaynak koleksiyon hiç etkilenmez.
        /// </summary>
        public void SetColumns(int columns)
        {
            columns = Math.Max(1, columns);
            if (columns == _columns) return;
            _columns = columns;
            RebuildAll();
        }

        private void RebuildAll()
        {
            Clear();
            for (int i = 0; i < _source.Count; i += _columns)
            {
                var row = new CardRow<T>();
                int end = Math.Min(i + _columns, _source.Count);
                for (int j = i; j < end; j++)
                    row.Items.Add(_source[j]);
                Add(row);
            }
        }

        private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            try
            {
                switch (e.Action)
                {
                    case NotifyCollectionChangedAction.Reset:
                        RebuildAll();
                        break;

                    case NotifyCollectionChangedAction.Add
                        when e.NewItems != null &&
                             e.NewStartingIndex == _source.Count - e.NewItems.Count:
                        // Sondan ekleme (sayfalama / LoadMoreItems) — sadece
                        // etkilenen tek yeni öğe(ler) işlenir, tüm liste
                        // yeniden taranmaz.
                        foreach (var item in e.NewItems)
                            AppendFlat((T)item!);
                        break;

                    case NotifyCollectionChangedAction.Remove
                        when e.OldItems != null &&
                             e.OldStartingIndex == _source.Count:
                        // Sondan silme (ReplaceCollection'ın col.RemoveAt(col.Count-1)
                        // döngüsü — her zaman en sondaki öğeyi siler).
                        for (int k = 0; k < e.OldItems.Count; k++)
                            RemoveLastFlat();
                        break;

                    case NotifyCollectionChangedAction.Replace
                        when e.NewItems != null && e.NewItems.Count == 1 &&
                             e.NewStartingIndex >= 0:
                        // Aynı indexte değer değişimi (ReplaceCollection'ın
                        // indexer set çağrısı, örn. col[i] = list[i]).
                        ReplaceFlat(e.NewStartingIndex, (T)e.NewItems[0]!);
                        break;

                    default:
                        // Beklenmeyen/karma bir değişiklik — güvenli taraf:
                        // tam yeniden inşa. Nadiren tetiklenir (şu anki kod
                        // tabanında hiç tetiklenmez), doğruluğu her zaman
                        // korur.
                        RebuildAll();
                        break;
                }
            }
            catch
            {
                // Herhangi bir index/durum tutarsızlığında sessizce tam
                // yeniden inşaya düş — UI hiçbir zaman bozuk/yanlış kalmaz.
                RebuildAll();
            }
        }

        private void AppendFlat(T item)
        {
            if (Count > 0 && this[Count - 1].Items.Count < _columns)
            {
                this[Count - 1].Items.Add(item);
            }
            else
            {
                var row = new CardRow<T>();
                row.Items.Add(item);
                Add(row);
            }
        }

        private void RemoveLastFlat()
        {
            if (Count == 0) return;
            var lastRow = this[Count - 1];
            if (lastRow.Items.Count == 0) { RemoveAt(Count - 1); return; }

            lastRow.Items.RemoveAt(lastRow.Items.Count - 1);
            if (lastRow.Items.Count == 0)
                RemoveAt(Count - 1);
        }

        private void ReplaceFlat(int flatIndex, T value)
        {
            int rowIdx = flatIndex / _columns;
            int colIdx = flatIndex % _columns;

            if (rowIdx < 0 || rowIdx >= Count) { RebuildAll(); return; }
            var row = this[rowIdx];
            if (colIdx < 0 || colIdx >= row.Items.Count) { RebuildAll(); return; }

            row.Items[colIdx] = value;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _source.CollectionChanged -= OnSourceChanged;
        }
    }
}
