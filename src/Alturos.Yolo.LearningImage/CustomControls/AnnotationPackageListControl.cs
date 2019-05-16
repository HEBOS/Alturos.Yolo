﻿using Alturos.Yolo.LearningImage.Contract;
using Alturos.Yolo.LearningImage.Helper;
using Alturos.Yolo.LearningImage.Model;
using log4net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Alturos.Yolo.LearningImage.CustomControls
{
    public partial class AnnotationPackageListControl : UserControl
    {
        private static ILog Log = LogManager.GetLogger(typeof(AnnotationPackageListControl));

        public Action<AnnotationPackage> PackageSelected { get; set; }

        public DataGridView DataGridView { get { return this.dataGridView1; } }

        private IBoundingBoxReader _boundingBoxReader;
        private IAnnotationPackageProvider _annotationPackageProvider;
        private List<ObjectClass> _objectClasses;

        public AnnotationPackageListControl()
        {
            this.InitializeComponent();
            this.dataGridView1.AutoGenerateColumns = false;
            this.labelLoading.Location = new Point(5, 20);
        }

        public void Setup(IBoundingBoxReader boundingBoxReader, IAnnotationPackageProvider annotationPackageProvider, List<ObjectClass> objectClasses)
        {
            this._boundingBoxReader = boundingBoxReader;
            this._annotationPackageProvider = annotationPackageProvider;
            this._objectClasses = objectClasses;
        }

        public AnnotationPackage[] GetAllPackages()
        {
            var items = new List<AnnotationPackage>();

            foreach (DataGridViewRow row in this.dataGridView1.Rows)
            {
                var package = row.DataBoundItem as AnnotationPackage;
                items.Add(package);
            }

            return items.ToArray();
        }

        public AnnotationImage[] GetAllImages()
        {
            var items = new List<AnnotationImage>();

            foreach (DataGridViewRow row in this.dataGridView1.Rows)
            {
                var package = row.DataBoundItem as AnnotationPackage;
                if (package.Extracted && package.Images != null)
                {
                    items.AddRange(package.Images);
                }
            }

            return items.ToArray();
        }

        public async Task LoadPackagesAsync()
        {
            this.labelLoading.Invoke((MethodInvoker)delegate { this.labelLoading.Visible = true; });
            this.dataGridView1.Invoke((MethodInvoker)delegate { this.dataGridView1.Visible = false; });

            var packages = await this._annotationPackageProvider.GetPackagesAsync();

            this.labelLoading.Invoke((MethodInvoker)delegate { this.labelLoading.Visible = false; });

            foreach (var package in packages)
            {
                if (package.Extracted && package.Images == null)
                {
                    this.LoadAnnotationImages(package);
                }
            }

            if (packages?.Length > 0)
            {
                this.dataGridView1.Invoke((MethodInvoker)delegate
                {
                    this.dataGridView1.Visible = true;
                    this.dataGridView1.DataSource = packages;
                });
            }
        }

        public void LoadAnnotationImages(AnnotationPackage package)
        {
            if (!package.Extracted)
            {
                return;
            }

            var allowedImageFormats = new string[] { ".png" , ".jpg", ".bmp" };

            var files = Directory.GetFiles(package.PackagePath, "*.*", SearchOption.TopDirectoryOnly).Select(file => new FileInfo(file));
            var items = files.Where(file => allowedImageFormats.Contains(file.Extension)).Select(o => new AnnotationImage
            {
                FilePath = o.FullName,
                DisplayName = o.Name,
                BoundingBoxes = this._boundingBoxReader.GetBoxes(this._boundingBoxReader.GetDataPath(o.FullName)).ToList()
            }).ToList();

            if (items.Count == 0)
            {
                return;
            }

            package.Images = items;
            this.UpdateAnnotationPercentage(package);
        }

        public void UpdateAnnotationPercentage(AnnotationPackage package)
        {
            var annotatedImageCount = 0;

            foreach (var image in package.Images)
            {
                if (image.BoundingBoxes?.Count > 0)
                {
                    annotatedImageCount++;
                }
            }

            package.Info.AnnotationPercentage = annotatedImageCount / ((double)package.Images.Count) * 100;
        }

        public void UnzipPackage(AnnotationPackage package)
        {
            var zipFilePath = package.PackagePath;

            var extractedPackagePath = Path.Combine(Path.GetDirectoryName(zipFilePath), Path.GetFileNameWithoutExtension(zipFilePath));
            if (Directory.Exists(extractedPackagePath))
            {
                Directory.Delete(extractedPackagePath, true);
            }

            ZipFile.ExtractToDirectory(package.PackagePath, extractedPackagePath);
            File.Delete(zipFilePath);

            package.Extracted = true;
            package.PackagePath = extractedPackagePath;
        }

        public void SetAnnotationMetadata(AnnotationPackage package)
        {
            if (package.Info.ImageDtos != null)
            {
                // Clean up
                var files = Directory.GetFiles(package.PackagePath, "*.txt");
                foreach (var file in files)
                {
                    File.Delete(file);
                }

                // Write annotation metadata
                var customCulture = (CultureInfo)System.Threading.Thread.CurrentThread.CurrentCulture.Clone();
                customCulture.NumberFormat.NumberDecimalSeparator = ".";

                System.Threading.Thread.CurrentThread.CurrentCulture = customCulture;

                foreach (var imageDto in package.Info.ImageDtos.Where(o => o.BoundingBoxes?.Count > 0))
                {
                    var sb = new StringBuilder();
                    foreach (var boundingBox in imageDto.BoundingBoxes)
                    {
                        sb.Append(boundingBox.ObjectIndex).Append(" ");
                        sb.Append(boundingBox.CenterX).Append(" ");
                        sb.Append(boundingBox.CenterY).Append(" ");
                        sb.Append(boundingBox.Width).Append(" ");
                        sb.Append(boundingBox.Height).AppendLine();
                    }

                    var dataPath = this._boundingBoxReader.GetDataPath(imageDto.FilePath);
                    File.WriteAllText(dataPath, sb.ToString());
                }
            }
        }

        private void dataGridView1_SelectionChanged(object sender, EventArgs e)
        {
            var package = this.dataGridView1.CurrentRow.DataBoundItem as AnnotationPackage;
            this.PackageSelected?.Invoke(package);
        }

        private async void redownloadToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.PackageSelected?.Invoke(null);

            var package = this.dataGridView1.Rows[this.dataGridView1.CurrentCell.RowIndex].DataBoundItem as AnnotationPackage;

            var downloadedPackage = await this._annotationPackageProvider.RefreshPackageAsync(package);
            this.UnzipPackage(downloadedPackage);

            downloadedPackage.Images = null;
            this.PackageSelected?.Invoke(downloadedPackage);
        }

        private void ChangeObjectClassIndices(AnnotationPackage package, bool toYoloMark)
        {
            // Lookup table to convert Yolo Mark indices to our indices or vice-versa
            var oldNewIndexCollection = new Dictionary<int, int>();
            for (var i = 0; i < this._objectClasses.Count; i++)
            {
                if (toYoloMark)
                {
                    oldNewIndexCollection[this._objectClasses[i].Id] = i;
                }
                else
                {
                    oldNewIndexCollection[i] = this._objectClasses[i].Id;
                }
            }

            var files = Directory.GetFiles(package.PackagePath).Where(o => o.EndsWith(".txt"));
            foreach (var file in files)
            {
                var lines = File.ReadAllLines(file);
                var sb = new StringBuilder();

                foreach (var line in lines)
                {
                    var index = line.GetFirstNumber();

                    try
                    {
                        sb.AppendLine(line.ReplaceFirst(index.ToString(), oldNewIndexCollection[index].ToString()));
                    }
                    catch (KeyNotFoundException exception)
                    {
                        Log.Error($"{nameof(ChangeObjectClassIndices)} - key: {index.ToString()}, toYoloMark: {toYoloMark}", exception);
                    }
                }

                File.WriteAllText(file, sb.ToString());
            }
        }

        private void dataGridView1_RowPrePaint(object sender, DataGridViewRowPrePaintEventArgs e)
        {
            var item = this.dataGridView1.Rows[e.RowIndex].DataBoundItem as AnnotationPackage;

            if (item.Info.IsAnnotated)
            {
                this.dataGridView1.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.GreenYellow;
                return;
            }

            if (item.Extracted)
            {
                this.dataGridView1.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.LightBlue;
                return;
            }

            this.dataGridView1.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.White;
        }
    }
}
