; The installer's own text in every language of the app window (Krupno.iss includes this file). The model titles are the ones
; the window shows (src/Krupno.App/Properties/Strings*.resx); the rest of the wizard comes from Inno Setup's messages files.
; LocalizationTests checks that every language has every message.

[CustomMessages]

; English
en.TypeFull=Full installation (all models)
en.TypeCompact=Compact installation (recommended model only)
en.TypeCustom=Custom installation
en.ComponentApp=Krupno (desktop app and krupno-cli command-line tool)
en.ComponentModels=Models (downloaded from GitHub during installation)
en.GroupCommandLine=Command line:
en.TaskPath=Add the install folder to PATH, so krupno-cli can be run from any console
en.GroupExplorer=Explorer integration:
en.ContextMenu=Upscale with Krupno
en.TaskContextMenu=Add “Upscale with Krupno” to the Explorer context menu of images and folders
en.ModelRealPlksr=Photo, fast: BHI RealPLKSR (CC BY 4.0) · Recommended
en.ModelDat2Fp16=Photo, maximum detail: BHI DAT2 fp16 (CC BY 4.0)
en.ModelUltraSharp=General purpose: UltraSharpV2 (CC BY-NC-SA 4.0) · Non-commercial
en.ModelRemacri=Text and documents: Remacri fp16 (CC BY-NC-SA 4.0) · Non-commercial
en.ModelDat2=Photo, maximum detail: BHI DAT2 fp32 (CC BY 4.0) · very slow
en.ModelEsrganFp16=Classic: Real-ESRGAN x4plus fp16 (BSD-3-Clause)
en.ModelEsrgan=Classic: Real-ESRGAN x4plus fp32 (BSD-3-Clause)
en.ModelGeneral=Draft: Real-ESRGAN general x4v3 (BSD-3-Clause) · very fast
en.ModelDenoise=Remove noise: SCUNet fp16 (Apache-2.0) · for noisy photos

; Українська
uk.TypeFull=Повне встановлення (усі моделі)
uk.TypeCompact=Компактне встановлення (лише рекомендована модель)
uk.TypeCustom=Вибіркове встановлення
uk.ComponentApp=Krupno (програма і консольний інструмент krupno-cli)
uk.ComponentModels=Моделі (завантажуються з GitHub під час встановлення)
uk.GroupCommandLine=Командний рядок:
uk.TaskPath=Додати теку встановлення до PATH, щоб krupno-cli запускався з будь-якої консолі
uk.GroupExplorer=Інтеграція з Провідником:
uk.ContextMenu=Збільшити в Krupno
uk.TaskContextMenu=Додати «Збільшити в Krupno» до контекстного меню зображень і тек у Провіднику
uk.ModelRealPlksr=Фото, швидко: BHI RealPLKSR (CC BY 4.0) · Рекомендовано
uk.ModelDat2Fp16=Фото, максимум деталей: BHI DAT2 fp16 (CC BY 4.0)
uk.ModelUltraSharp=Універсальна: UltraSharpV2 (CC BY-NC-SA 4.0) · Некомерційна
uk.ModelRemacri=Текст і документи: Remacri fp16 (CC BY-NC-SA 4.0) · Некомерційна
uk.ModelDat2=Фото, максимум деталей: BHI DAT2 fp32 (CC BY 4.0) · дуже повільно
uk.ModelEsrganFp16=Класика: Real-ESRGAN x4plus fp16 (BSD-3-Clause)
uk.ModelEsrgan=Класика: Real-ESRGAN x4plus fp32 (BSD-3-Clause)
uk.ModelGeneral=Чернетка: Real-ESRGAN general x4v3 (BSD-3-Clause) · дуже швидко
uk.ModelDenoise=Прибрати шум: SCUNet fp16 (Apache-2.0) · для шумних фото

; Русский
ru.TypeFull=Полная установка (все модели)
ru.TypeCompact=Компактная установка (только рекомендуемая модель)
ru.TypeCustom=Выборочная установка
ru.ComponentApp=Krupno (программа и консольный инструмент krupno-cli)
ru.ComponentModels=Модели (загружаются с GitHub во время установки)
ru.GroupCommandLine=Командная строка:
ru.TaskPath=Добавить папку установки в PATH, чтобы krupno-cli запускался из любой консоли
ru.GroupExplorer=Интеграция с Проводником:
ru.ContextMenu=Увеличить в Krupno
ru.TaskContextMenu=Добавить «Увеличить в Krupno» в контекстное меню изображений и папок в Проводнике
ru.ModelRealPlksr=Фото, быстро: BHI RealPLKSR (CC BY 4.0) · Рекомендуется
ru.ModelDat2Fp16=Фото, максимум деталей: BHI DAT2 fp16 (CC BY 4.0)
ru.ModelUltraSharp=Универсальная: UltraSharpV2 (CC BY-NC-SA 4.0) · Некоммерческая
ru.ModelRemacri=Текст и документы: Remacri fp16 (CC BY-NC-SA 4.0) · Некоммерческая
ru.ModelDat2=Фото, максимум деталей: BHI DAT2 fp32 (CC BY 4.0) · очень медленно
ru.ModelEsrganFp16=Классика: Real-ESRGAN x4plus fp16 (BSD-3-Clause)
ru.ModelEsrgan=Классика: Real-ESRGAN x4plus fp32 (BSD-3-Clause)
ru.ModelGeneral=Черновик: Real-ESRGAN general x4v3 (BSD-3-Clause) · очень быстро
ru.ModelDenoise=Убрать шум: SCUNet fp16 (Apache-2.0) · для шумных фото

; Polski
pl.TypeFull=Instalacja pełna (wszystkie modele)
pl.TypeCompact=Instalacja minimalna (tylko zalecany model)
pl.TypeCustom=Instalacja niestandardowa
pl.ComponentApp=Krupno (aplikacja i narzędzie wiersza poleceń krupno-cli)
pl.ComponentModels=Modele (pobierane z GitHub podczas instalacji)
pl.GroupCommandLine=Wiersz poleceń:
pl.TaskPath=Dodaj folder instalacji do zmiennej PATH, aby uruchamiać krupno-cli z dowolnej konsoli
pl.GroupExplorer=Integracja z Eksploratorem:
pl.ContextMenu=Powiększ w Krupno
pl.TaskContextMenu=Dodaj „Powiększ w Krupno” do menu kontekstowego obrazów i folderów w Eksploratorze
pl.ModelRealPlksr=Zdjęcia, szybko: BHI RealPLKSR (CC BY 4.0) · Zalecany
pl.ModelDat2Fp16=Zdjęcia, maksimum szczegółów: BHI DAT2 fp16 (CC BY 4.0)
pl.ModelUltraSharp=Uniwersalny: UltraSharpV2 (CC BY-NC-SA 4.0) · Niekomercyjny
pl.ModelRemacri=Tekst i dokumenty: Remacri fp16 (CC BY-NC-SA 4.0) · Niekomercyjny
pl.ModelDat2=Zdjęcia, maksimum szczegółów: BHI DAT2 fp32 (CC BY 4.0) · bardzo wolny
pl.ModelEsrganFp16=Klasyczny: Real-ESRGAN x4plus fp16 (BSD-3-Clause)
pl.ModelEsrgan=Klasyczny: Real-ESRGAN x4plus fp32 (BSD-3-Clause)
pl.ModelGeneral=Wersja robocza: Real-ESRGAN general x4v3 (BSD-3-Clause) · bardzo szybki
pl.ModelDenoise=Usuń szum: SCUNet fp16 (Apache-2.0) · do zaszumionych zdjęć

; Deutsch
de.TypeFull=Vollständige Installation (alle Modelle)
de.TypeCompact=Minimale Installation (nur das empfohlene Modell)
de.TypeCustom=Benutzerdefinierte Installation
de.ComponentApp=Krupno (Desktop-App und Befehlszeilenprogramm krupno-cli)
de.ComponentModels=Modelle (werden bei der Installation von GitHub heruntergeladen)
de.GroupCommandLine=Befehlszeile:
de.TaskPath=Installationsordner zu PATH hinzufügen, damit krupno-cli in jeder Konsole läuft
de.GroupExplorer=Explorer-Integration:
de.ContextMenu=Mit Krupno hochskalieren
de.TaskContextMenu=„Mit Krupno hochskalieren“ zum Kontextmenü von Bildern und Ordnern im Explorer hinzufügen
de.ModelRealPlksr=Foto, schnell: BHI RealPLKSR (CC BY 4.0) · Empfohlen
de.ModelDat2Fp16=Foto, maximale Details: BHI DAT2 fp16 (CC BY 4.0)
de.ModelUltraSharp=Universell: UltraSharpV2 (CC BY-NC-SA 4.0) · Nicht kommerziell
de.ModelRemacri=Text und Dokumente: Remacri fp16 (CC BY-NC-SA 4.0) · Nicht kommerziell
de.ModelDat2=Foto, maximale Details: BHI DAT2 fp32 (CC BY 4.0) · sehr langsam
de.ModelEsrganFp16=Klassisch: Real-ESRGAN x4plus fp16 (BSD-3-Clause)
de.ModelEsrgan=Klassisch: Real-ESRGAN x4plus fp32 (BSD-3-Clause)
de.ModelGeneral=Entwurf: Real-ESRGAN general x4v3 (BSD-3-Clause) · sehr schnell
de.ModelDenoise=Rauschen entfernen: SCUNet fp16 (Apache-2.0) · für verrauschte Fotos

; Français
fr.TypeFull=Installation complète (tous les modèles)
fr.TypeCompact=Installation compacte (modèle recommandé uniquement)
fr.TypeCustom=Installation personnalisée
fr.ComponentApp=Krupno (application et outil en ligne de commande krupno-cli)
fr.ComponentModels=Modèles (téléchargés depuis GitHub pendant l'installation)
fr.GroupCommandLine=Ligne de commande :
fr.TaskPath=Ajouter le dossier d'installation au PATH pour lancer krupno-cli depuis n'importe quelle console
fr.GroupExplorer=Intégration à l'Explorateur :
fr.ContextMenu=Agrandir avec Krupno
fr.TaskContextMenu=Ajouter « Agrandir avec Krupno » au menu contextuel des images et dossiers dans l'Explorateur
fr.ModelRealPlksr=Photo, rapide : BHI RealPLKSR (CC BY 4.0) · Recommandé
fr.ModelDat2Fp16=Photo, détails maximum : BHI DAT2 fp16 (CC BY 4.0)
fr.ModelUltraSharp=Usage général : UltraSharpV2 (CC BY-NC-SA 4.0) · Non commercial
fr.ModelRemacri=Texte et documents : Remacri fp16 (CC BY-NC-SA 4.0) · Non commercial
fr.ModelDat2=Photo, détails maximum : BHI DAT2 fp32 (CC BY 4.0) · très lent
fr.ModelEsrganFp16=Classique : Real-ESRGAN x4plus fp16 (BSD-3-Clause)
fr.ModelEsrgan=Classique : Real-ESRGAN x4plus fp32 (BSD-3-Clause)
fr.ModelGeneral=Brouillon : Real-ESRGAN general x4v3 (BSD-3-Clause) · très rapide
fr.ModelDenoise=Supprimer le bruit : SCUNet fp16 (Apache-2.0) · pour les photos bruitées

; Italiano
it.TypeFull=Installazione completa (tutti i modelli)
it.TypeCompact=Installazione compatta (solo il modello consigliato)
it.TypeCustom=Installazione personalizzata
it.ComponentApp=Krupno (app desktop e strumento da riga di comando krupno-cli)
it.ComponentModels=Modelli (scaricati da GitHub durante l'installazione)
it.GroupCommandLine=Riga di comando:
it.TaskPath=Aggiungi la cartella di installazione al PATH, per eseguire krupno-cli da qualsiasi console
it.GroupExplorer=Integrazione con Esplora file:
it.ContextMenu=Ingrandisci con Krupno
it.TaskContextMenu=Aggiungi «Ingrandisci con Krupno» al menu contestuale di immagini e cartelle in Esplora file
it.ModelRealPlksr=Foto, veloce: BHI RealPLKSR (CC BY 4.0) · Consigliato
it.ModelDat2Fp16=Foto, massimo dettaglio: BHI DAT2 fp16 (CC BY 4.0)
it.ModelUltraSharp=Uso generale: UltraSharpV2 (CC BY-NC-SA 4.0) · Non commerciale
it.ModelRemacri=Testo e documenti: Remacri fp16 (CC BY-NC-SA 4.0) · Non commerciale
it.ModelDat2=Foto, massimo dettaglio: BHI DAT2 fp32 (CC BY 4.0) · molto lento
it.ModelEsrganFp16=Classico: Real-ESRGAN x4plus fp16 (BSD-3-Clause)
it.ModelEsrgan=Classico: Real-ESRGAN x4plus fp32 (BSD-3-Clause)
it.ModelGeneral=Bozza: Real-ESRGAN general x4v3 (BSD-3-Clause) · molto veloce
it.ModelDenoise=Rimuovi il rumore: SCUNet fp16 (Apache-2.0) · per le foto rumorose

; Español
es.TypeFull=Instalación completa (todos los modelos)
es.TypeCompact=Instalación compacta (solo el modelo recomendado)
es.TypeCustom=Instalación personalizada
es.ComponentApp=Krupno (aplicación y herramienta de línea de comandos krupno-cli)
es.ComponentModels=Modelos (se descargan de GitHub durante la instalación)
es.GroupCommandLine=Línea de comandos:
es.TaskPath=Añadir la carpeta de instalación al PATH para ejecutar krupno-cli desde cualquier consola
es.GroupExplorer=Integración con el Explorador:
es.ContextMenu=Ampliar con Krupno
es.TaskContextMenu=Añadir «Ampliar con Krupno» al menú contextual de imágenes y carpetas del Explorador
es.ModelRealPlksr=Foto, rápido: BHI RealPLKSR (CC BY 4.0) · Recomendado
es.ModelDat2Fp16=Foto, máximo detalle: BHI DAT2 fp16 (CC BY 4.0)
es.ModelUltraSharp=Uso general: UltraSharpV2 (CC BY-NC-SA 4.0) · No comercial
es.ModelRemacri=Texto y documentos: Remacri fp16 (CC BY-NC-SA 4.0) · No comercial
es.ModelDat2=Foto, máximo detalle: BHI DAT2 fp32 (CC BY 4.0) · muy lento
es.ModelEsrganFp16=Clásico: Real-ESRGAN x4plus fp16 (BSD-3-Clause)
es.ModelEsrgan=Clásico: Real-ESRGAN x4plus fp32 (BSD-3-Clause)
es.ModelGeneral=Borrador: Real-ESRGAN general x4v3 (BSD-3-Clause) · muy rápido
es.ModelDenoise=Eliminar el ruido: SCUNet fp16 (Apache-2.0) · para fotos con ruido

; Português (Brasil)
ptbr.TypeFull=Instalação completa (todos os modelos)
ptbr.TypeCompact=Instalação compacta (somente o modelo recomendado)
ptbr.TypeCustom=Instalação personalizada
ptbr.ComponentApp=Krupno (aplicativo e ferramenta de linha de comando krupno-cli)
ptbr.ComponentModels=Modelos (baixados do GitHub durante a instalação)
ptbr.GroupCommandLine=Linha de comando:
ptbr.TaskPath=Adicionar a pasta de instalação ao PATH para executar o krupno-cli em qualquer console
ptbr.GroupExplorer=Integração com o Explorador:
ptbr.ContextMenu=Ampliar com o Krupno
ptbr.TaskContextMenu=Adicionar “Ampliar com o Krupno” ao menu de contexto de imagens e pastas no Explorador
ptbr.ModelRealPlksr=Foto, rápido: BHI RealPLKSR (CC BY 4.0) · Recomendado
ptbr.ModelDat2Fp16=Foto, máximo de detalhes: BHI DAT2 fp16 (CC BY 4.0)
ptbr.ModelUltraSharp=Uso geral: UltraSharpV2 (CC BY-NC-SA 4.0) · Não comercial
ptbr.ModelRemacri=Texto e documentos: Remacri fp16 (CC BY-NC-SA 4.0) · Não comercial
ptbr.ModelDat2=Foto, máximo de detalhes: BHI DAT2 fp32 (CC BY 4.0) · muito lento
ptbr.ModelEsrganFp16=Clássico: Real-ESRGAN x4plus fp16 (BSD-3-Clause)
ptbr.ModelEsrgan=Clássico: Real-ESRGAN x4plus fp32 (BSD-3-Clause)
ptbr.ModelGeneral=Rascunho: Real-ESRGAN general x4v3 (BSD-3-Clause) · muito rápido
ptbr.ModelDenoise=Remover ruído: SCUNet fp16 (Apache-2.0) · para fotos com ruído

; Türkçe
tr.TypeFull=Tam kurulum (tüm modeller)
tr.TypeCompact=Kompakt kurulum (yalnızca önerilen model)
tr.TypeCustom=Özel kurulum
tr.ComponentApp=Krupno (masaüstü uygulaması ve krupno-cli komut satırı aracı)
tr.ComponentModels=Modeller (kurulum sırasında GitHub'dan indirilir)
tr.GroupCommandLine=Komut satırı:
tr.TaskPath=krupno-cli her konsoldan çalışsın diye kurulum klasörünü PATH'e ekle
tr.GroupExplorer=Dosya Gezgini tümleştirmesi:
tr.ContextMenu=Krupno ile büyüt
tr.TaskContextMenu=Dosya Gezgini'nde resim ve klasörlerin bağlam menüsüne “Krupno ile büyüt” ekle
tr.ModelRealPlksr=Fotoğraf, hızlı: BHI RealPLKSR (CC BY 4.0) · Önerilen
tr.ModelDat2Fp16=Fotoğraf, en fazla ayrıntı: BHI DAT2 fp16 (CC BY 4.0)
tr.ModelUltraSharp=Genel amaçlı: UltraSharpV2 (CC BY-NC-SA 4.0) · Ticari olmayan
tr.ModelRemacri=Metin ve belgeler: Remacri fp16 (CC BY-NC-SA 4.0) · Ticari olmayan
tr.ModelDat2=Fotoğraf, en fazla ayrıntı: BHI DAT2 fp32 (CC BY 4.0) · çok yavaş
tr.ModelEsrganFp16=Klasik: Real-ESRGAN x4plus fp16 (BSD-3-Clause)
tr.ModelEsrgan=Klasik: Real-ESRGAN x4plus fp32 (BSD-3-Clause)
tr.ModelGeneral=Taslak: Real-ESRGAN general x4v3 (BSD-3-Clause) · çok hızlı
tr.ModelDenoise=Gürültüyü gider: SCUNet fp16 (Apache-2.0) · gürültülü fotoğraflar için

; Bahasa Indonesia
id.TypeFull=Instalasi penuh (semua model)
id.TypeCompact=Instalasi ringkas (hanya model yang disarankan)
id.TypeCustom=Instalasi kustom
id.ComponentApp=Krupno (aplikasi desktop dan alat baris perintah krupno-cli)
id.ComponentModels=Model (diunduh dari GitHub saat instalasi)
id.GroupCommandLine=Baris perintah:
id.TaskPath=Tambahkan folder instalasi ke PATH agar krupno-cli dapat dijalankan dari konsol mana pun
id.GroupExplorer=Integrasi dengan File Explorer:
id.ContextMenu=Perbesar dengan Krupno
id.TaskContextMenu=Tambahkan “Perbesar dengan Krupno” ke menu konteks gambar dan folder di File Explorer
id.ModelRealPlksr=Foto, cepat: BHI RealPLKSR (CC BY 4.0) · Direkomendasikan
id.ModelDat2Fp16=Foto, detail maksimum: BHI DAT2 fp16 (CC BY 4.0)
id.ModelUltraSharp=Serbaguna: UltraSharpV2 (CC BY-NC-SA 4.0) · Nonkomersial
id.ModelRemacri=Teks dan dokumen: Remacri fp16 (CC BY-NC-SA 4.0) · Nonkomersial
id.ModelDat2=Foto, detail maksimum: BHI DAT2 fp32 (CC BY 4.0) · sangat lambat
id.ModelEsrganFp16=Klasik: Real-ESRGAN x4plus fp16 (BSD-3-Clause)
id.ModelEsrgan=Klasik: Real-ESRGAN x4plus fp32 (BSD-3-Clause)
id.ModelGeneral=Draf: Real-ESRGAN general x4v3 (BSD-3-Clause) · sangat cepat
id.ModelDenoise=Hapus noise: SCUNet fp16 (Apache-2.0) · untuk foto yang bernoise

; Tiếng Việt
vi.TypeFull=Cài đặt đầy đủ (tất cả mô hình)
vi.TypeCompact=Cài đặt gọn (chỉ mô hình được đề xuất)
vi.TypeCustom=Cài đặt tùy chỉnh
vi.ComponentApp=Krupno (ứng dụng và công cụ dòng lệnh krupno-cli)
vi.ComponentModels=Mô hình (tải từ GitHub trong khi cài đặt)
vi.GroupCommandLine=Dòng lệnh:
vi.TaskPath=Thêm thư mục cài đặt vào PATH để chạy krupno-cli từ mọi cửa sổ dòng lệnh
vi.GroupExplorer=Tích hợp với File Explorer:
vi.ContextMenu=Phóng to bằng Krupno
vi.TaskContextMenu=Thêm “Phóng to bằng Krupno” vào menu ngữ cảnh của ảnh và thư mục trong File Explorer
vi.ModelRealPlksr=Ảnh chụp, nhanh: BHI RealPLKSR (CC BY 4.0) · Khuyên dùng
vi.ModelDat2Fp16=Ảnh chụp, chi tiết tối đa: BHI DAT2 fp16 (CC BY 4.0)
vi.ModelUltraSharp=Đa năng: UltraSharpV2 (CC BY-NC-SA 4.0) · Phi thương mại
vi.ModelRemacri=Văn bản và tài liệu: Remacri fp16 (CC BY-NC-SA 4.0) · Phi thương mại
vi.ModelDat2=Ảnh chụp, chi tiết tối đa: BHI DAT2 fp32 (CC BY 4.0) · rất chậm
vi.ModelEsrganFp16=Cổ điển: Real-ESRGAN x4plus fp16 (BSD-3-Clause)
vi.ModelEsrgan=Cổ điển: Real-ESRGAN x4plus fp32 (BSD-3-Clause)
vi.ModelGeneral=Bản nháp: Real-ESRGAN general x4v3 (BSD-3-Clause) · rất nhanh
vi.ModelDenoise=Khử nhiễu: SCUNet fp16 (Apache-2.0) · cho ảnh bị nhiễu

; 日本語
ja.TypeFull=フル インストール（すべてのモデル）
ja.TypeCompact=コンパクト インストール（推奨モデルのみ）
ja.TypeCustom=カスタム インストール
ja.ComponentApp=Krupno（デスクトップ アプリとコマンドライン ツール krupno-cli）
ja.ComponentModels=モデル（インストール中に GitHub からダウンロード）
ja.GroupCommandLine=コマンドライン:
ja.TaskPath=インストール フォルダーを PATH に追加して、どのコンソールからでも krupno-cli を実行できるようにする
ja.GroupExplorer=エクスプローラーとの統合:
ja.ContextMenu=Krupno で拡大
ja.TaskContextMenu=エクスプローラーの画像とフォルダーの右クリック メニューに「Krupno で拡大」を追加する
ja.ModelRealPlksr=写真、高速：BHI RealPLKSR (CC BY 4.0) · 推奨
ja.ModelDat2Fp16=写真、最大限の精細さ：BHI DAT2 fp16 (CC BY 4.0)
ja.ModelUltraSharp=汎用：UltraSharpV2 (CC BY-NC-SA 4.0) · 非営利
ja.ModelRemacri=文字・文書：Remacri fp16 (CC BY-NC-SA 4.0) · 非営利
ja.ModelDat2=写真、最大限の精細さ：BHI DAT2 fp32 (CC BY 4.0) · 非常に低速
ja.ModelEsrganFp16=クラシック：Real-ESRGAN x4plus fp16 (BSD-3-Clause)
ja.ModelEsrgan=クラシック：Real-ESRGAN x4plus fp32 (BSD-3-Clause)
ja.ModelGeneral=下書き：Real-ESRGAN general x4v3 (BSD-3-Clause) · 非常に高速
ja.ModelDenoise=ノイズを除去：SCUNet fp16 (Apache-2.0) · ノイズの多い写真向け

; 한국어
ko.TypeFull=전체 설치(모든 모델)
ko.TypeCompact=최소 설치(권장 모델만)
ko.TypeCustom=사용자 지정 설치
ko.ComponentApp=Krupno(데스크톱 앱 및 명령줄 도구 krupno-cli)
ko.ComponentModels=모델(설치 중 GitHub에서 다운로드)
ko.GroupCommandLine=명령줄:
ko.TaskPath=설치 폴더를 PATH에 추가하여 모든 콘솔에서 krupno-cli 실행
ko.GroupExplorer=파일 탐색기 통합:
ko.ContextMenu=Krupno로 업스케일
ko.TaskContextMenu=파일 탐색기의 이미지 및 폴더 바로 가기 메뉴에 'Krupno로 업스케일' 추가
ko.ModelRealPlksr=사진, 빠름: BHI RealPLKSR (CC BY 4.0) · 권장
ko.ModelDat2Fp16=사진, 최대 디테일: BHI DAT2 fp16 (CC BY 4.0)
ko.ModelUltraSharp=범용: UltraSharpV2 (CC BY-NC-SA 4.0) · 비상업용
ko.ModelRemacri=텍스트 및 문서: Remacri fp16 (CC BY-NC-SA 4.0) · 비상업용
ko.ModelDat2=사진, 최대 디테일: BHI DAT2 fp32 (CC BY 4.0) · 매우 느림
ko.ModelEsrganFp16=클래식: Real-ESRGAN x4plus fp16 (BSD-3-Clause)
ko.ModelEsrgan=클래식: Real-ESRGAN x4plus fp32 (BSD-3-Clause)
ko.ModelGeneral=초안: Real-ESRGAN general x4v3 (BSD-3-Clause) · 매우 빠름
ko.ModelDenoise=노이즈 제거: SCUNet fp16 (Apache-2.0) · 노이즈가 많은 사진용

; 简体中文
zhhans.TypeFull=完整安装（全部模型）
zhhans.TypeCompact=精简安装（仅推荐模型）
zhhans.TypeCustom=自定义安装
zhhans.ComponentApp=Krupno（桌面应用和命令行工具 krupno-cli）
zhhans.ComponentModels=模型（安装时从 GitHub 下载）
zhhans.GroupCommandLine=命令行：
zhhans.TaskPath=将安装文件夹添加到 PATH，以便在任意控制台运行 krupno-cli
zhhans.GroupExplorer=资源管理器集成：
zhhans.ContextMenu=用 Krupno 放大
zhhans.TaskContextMenu=在资源管理器中图像和文件夹的右键菜单添加“用 Krupno 放大”
zhhans.ModelRealPlksr=照片，快速：BHI RealPLKSR (CC BY 4.0) · 推荐
zhhans.ModelDat2Fp16=照片，最多细节：BHI DAT2 fp16 (CC BY 4.0)
zhhans.ModelUltraSharp=通用：UltraSharpV2 (CC BY-NC-SA 4.0) · 非商业
zhhans.ModelRemacri=文字和文档：Remacri fp16 (CC BY-NC-SA 4.0) · 非商业
zhhans.ModelDat2=照片，最多细节：BHI DAT2 fp32 (CC BY 4.0) · 很慢
zhhans.ModelEsrganFp16=经典：Real-ESRGAN x4plus fp16 (BSD-3-Clause)
zhhans.ModelEsrgan=经典：Real-ESRGAN x4plus fp32 (BSD-3-Clause)
zhhans.ModelGeneral=草稿：Real-ESRGAN general x4v3 (BSD-3-Clause) · 很快
zhhans.ModelDenoise=去除噪点：SCUNet fp16 (Apache-2.0) · 适用于有噪点的照片

; 繁體中文
zhhant.TypeFull=完整安裝（所有模型）
zhhant.TypeCompact=精簡安裝（僅建議的模型）
zhhant.TypeCustom=自訂安裝
zhhant.ComponentApp=Krupno（桌面應用程式與命令列工具 krupno-cli）
zhhant.ComponentModels=模型（安裝時從 GitHub 下載）
zhhant.GroupCommandLine=命令列：
zhhant.TaskPath=將安裝資料夾加入 PATH，以便在任何主控台執行 krupno-cli
zhhant.GroupExplorer=檔案總管整合：
zhhant.ContextMenu=用 Krupno 放大
zhhant.TaskContextMenu=在檔案總管中圖片和資料夾的右鍵選單加入「用 Krupno 放大」
zhhant.ModelRealPlksr=相片，快速：BHI RealPLKSR (CC BY 4.0) · 推薦
zhhant.ModelDat2Fp16=相片，最多細節：BHI DAT2 fp16 (CC BY 4.0)
zhhant.ModelUltraSharp=通用：UltraSharpV2 (CC BY-NC-SA 4.0) · 非商業
zhhant.ModelRemacri=文字和文件：Remacri fp16 (CC BY-NC-SA 4.0) · 非商業
zhhant.ModelDat2=相片，最多細節：BHI DAT2 fp32 (CC BY 4.0) · 非常慢
zhhant.ModelEsrganFp16=經典：Real-ESRGAN x4plus fp16 (BSD-3-Clause)
zhhant.ModelEsrgan=經典：Real-ESRGAN x4plus fp32 (BSD-3-Clause)
zhhant.ModelGeneral=草稿：Real-ESRGAN general x4v3 (BSD-3-Clause) · 非常快
zhhant.ModelDenoise=去除雜訊：SCUNet fp16 (Apache-2.0) · 適用於有雜訊的相片

; 繁體中文（香港）
zhhk.TypeFull=完整安裝（所有模型）
zhhk.TypeCompact=精簡安裝（僅建議的模型）
zhhk.TypeCustom=自訂安裝
zhhk.ComponentApp=Krupno（桌面應用程式與命令列工具 krupno-cli）
zhhk.ComponentModels=模型（安裝時從 GitHub 下載）
zhhk.GroupCommandLine=命令列：
zhhk.TaskPath=將安裝資料夾加入 PATH，以便在任何主控台執行 krupno-cli
zhhk.GroupExplorer=檔案總管整合：
zhhk.ContextMenu=用 Krupno 放大
zhhk.TaskContextMenu=在檔案總管中圖片和資料夾的右鍵選單加入「用 Krupno 放大」
zhhk.ModelRealPlksr=相片，快速：BHI RealPLKSR (CC BY 4.0) · 推薦
zhhk.ModelDat2Fp16=相片，最多細節：BHI DAT2 fp16 (CC BY 4.0)
zhhk.ModelUltraSharp=通用：UltraSharpV2 (CC BY-NC-SA 4.0) · 非商業
zhhk.ModelRemacri=文字和文件：Remacri fp16 (CC BY-NC-SA 4.0) · 非商業
zhhk.ModelDat2=相片，最多細節：BHI DAT2 fp32 (CC BY 4.0) · 非常慢
zhhk.ModelEsrganFp16=經典：Real-ESRGAN x4plus fp16 (BSD-3-Clause)
zhhk.ModelEsrgan=經典：Real-ESRGAN x4plus fp32 (BSD-3-Clause)
zhhk.ModelGeneral=草稿：Real-ESRGAN general x4v3 (BSD-3-Clause) · 非常快
zhhk.ModelDenoise=去除雜訊：SCUNet fp16 (Apache-2.0) · 適用於有雜訊的相片

; 繁體中文（澳門）
zhmo.TypeFull=完整安裝（所有模型）
zhmo.TypeCompact=精簡安裝（僅建議的模型）
zhmo.TypeCustom=自訂安裝
zhmo.ComponentApp=Krupno（桌面應用程式與命令列工具 krupno-cli）
zhmo.ComponentModels=模型（安裝時從 GitHub 下載）
zhmo.GroupCommandLine=命令列：
zhmo.TaskPath=將安裝資料夾加入 PATH，以便在任何主控台執行 krupno-cli
zhmo.GroupExplorer=檔案總管整合：
zhmo.ContextMenu=用 Krupno 放大
zhmo.TaskContextMenu=在檔案總管中圖片和資料夾的右鍵選單加入「用 Krupno 放大」
zhmo.ModelRealPlksr=相片，快速：BHI RealPLKSR (CC BY 4.0) · 推薦
zhmo.ModelDat2Fp16=相片，最多細節：BHI DAT2 fp16 (CC BY 4.0)
zhmo.ModelUltraSharp=通用：UltraSharpV2 (CC BY-NC-SA 4.0) · 非商業
zhmo.ModelRemacri=文字和文件：Remacri fp16 (CC BY-NC-SA 4.0) · 非商業
zhmo.ModelDat2=相片，最多細節：BHI DAT2 fp32 (CC BY 4.0) · 非常慢
zhmo.ModelEsrganFp16=經典：Real-ESRGAN x4plus fp16 (BSD-3-Clause)
zhmo.ModelEsrgan=經典：Real-ESRGAN x4plus fp32 (BSD-3-Clause)
zhmo.ModelGeneral=草稿：Real-ESRGAN general x4v3 (BSD-3-Clause) · 非常快
zhmo.ModelDenoise=去除雜訊：SCUNet fp16 (Apache-2.0) · 適用於有雜訊的相片
