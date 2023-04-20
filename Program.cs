using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Faithlife.Build;
using Faithlife.Utility;
using FuzzySharp;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using static Faithlife.Build.AppRunner;
using static Faithlife.Build.BuildUtility;

namespace rekordbox_obs
{
	class Program
	{
		static async Task Main(string[] args)
		{
			string EDMDirectory = "/Users/adam/Music/EDM";

			var songTagsLookup = new Dictionary<string, TagLib.File>();
			foreach (var songFile in Directory.EnumerateFiles(EDMDirectory, "*.*", SearchOption.AllDirectories))
			{
				try
				{
					var songTags = TagLib.File.Create(songFile);
					songTagsLookup.Add($"{songTags.Tag.Performers.Join(",")} - {songTags.Tag.Title}", songTags);
				}
				catch (TagLib.UnsupportedFormatException)
				{
				}
			}

			var processes = System.Diagnostics.Process.GetProcessesByName("rekordbox").ToList();
			if (processes.Count > 1)
				throw new InvalidOperationException("Multiple copies of rekordbox running!");

			var windowHandle = processes.First().MainWindowHandle;
			GetWindowRect(windowHandle, out var rect);

			var cancellationTokenSource = new CancellationTokenSource();
			Console.CancelKeyPress += (s, e) =>
			{
				e.Cancel = true;
				cancellationTokenSource.Cancel();
			};

			foreach (var file in FindFiles("*.png*", "*.txt"))
				File.Delete(file);

			var currentTrack = Track.None;
			var previousTrack = Track.None;
			while (true)
			{
				try
				{
					if (cancellationTokenSource.IsCancellationRequested)
						break;

					using var rekordboxScreenshot = ScreenShot(windowHandle, "rekordbox.png");

					using var leftSongInfo = GetSongInfoBitmap(rekordboxScreenshot, Track.Left);
					await leftSongInfo.SaveAsync("leftSongInfo.png").ConfigureAwait(false);
					Rectangle leftMasterRect;
					if (IsRetina)
					{
						// Get values for this
						leftMasterRect = GetNearestRectangle(leftSongInfo.Width - 100, 50, 100, 30);
					}
					else
					{
						leftMasterRect = GetNearestRectangle(leftSongInfo.Width - 50, 25, 50, 15);
					}
					using var leftMaster = leftSongInfo.Clone(x => x.Crop(leftMasterRect));
					await leftMaster.SaveAsync("leftMaster.png").ConfigureAwait(false);
					bool isLeftMaster = TrackIsMaster(leftMaster);

					using var rightSongInfo = GetSongInfoBitmap(rekordboxScreenshot, Track.Right);
					await rightSongInfo.SaveAsync("rightSongInfo.png").ConfigureAwait(false);
					Rectangle rightMasterRect;
					if (IsRetina)
					{
						// Get values for this
						rightMasterRect = GetNearestRectangle(rightSongInfo.Width - 100, 50, 100, 30);
					}
					else
					{
						rightMasterRect = GetNearestRectangle(rightSongInfo.Width - 50, 25, 50, 15);
					}
					using var rightMaster = rightSongInfo.Clone(x => x.Crop(rightMasterRect));
					await rightMaster.SaveAsync("rightMaster.png").ConfigureAwait(false);
					bool isRightMaster = TrackIsMaster(rightMaster);

					if (isLeftMaster)
					{
						currentTrack = Track.Left;
					}
					else if (isRightMaster)
					{
						currentTrack = Track.Right;
					}

					if (currentTrack != previousTrack)
					{
						previousTrack = currentTrack;

						await ProcessTrack(songTagsLookup, Track.Left, isLeftMaster, leftSongInfo).ConfigureAwait(false);
						await ProcessTrack(songTagsLookup, Track.Right, isRightMaster, rightSongInfo).ConfigureAwait(false);
					}
				}
				catch (Exception ex)
				{
					Console.WriteLine(ex.ToString());
				}
			}

			foreach (var file in FindFiles("*.png*", "*.txt"))
				File.Delete(file);
		}

		private static bool IsRetina => false;

		private static int GetNearestInt(double value)
		{
			return (int)Math.Floor(value);
		}

		private static Rectangle GetNearestRectangle(double x, double y, double width, double height)
		{
			return new Rectangle(GetNearestInt(x), GetNearestInt(y), GetNearestInt(width), GetNearestInt(height));
		}

		private static async Task ProcessTrack(Dictionary<string, TagLib.File> songTagsLookup, Track track, bool isTrackMaster, Image<Rgba32> songInfo)
		{
			var trackString = $"{track.ToString().ToLower()}Song";

			Rectangle songRect;
			if (IsRetina)
			{
				// Get values for this
				songRect = GetNearestRectangle(110, songInfo.Height * 0.1, songInfo.Width - 340, songInfo.Height * 0.9);
			}
			else
			{
				songRect = GetNearestRectangle(55, songInfo.Height * 0.1, songInfo.Width - 170, songInfo.Height * 0.9);
			}

			using var song = songInfo.Clone(x => x.Crop(songRect));
			var songImageFilename = $"{trackString}.png";
			await song.SaveAsync(songImageFilename).ConfigureAwait(false);

			string RunTesseractAndGetSongName()
			{
				RunApp("C:\\Program Files\\Tesseract-OCR\\tesseract.exe", new AppRunnerSettings
				{
					Arguments = new[] { "--psm", "3", songImageFilename, trackString },
				});
				var songName = File.ReadAllText($"{trackString}.txt");
				songName = songName.Replace("...", "");

				var match = Regex.Match(songName, "(.*)\n(.*) \\d+\\.\\d\\d", RegexOptions.Multiline);
				if (!match.Success)
					return null;

				var songMatch = Process.ExtractOne($"{match.Groups[1].Value} - {match.Groups[0].Value}", songTagsLookup.Keys);

				return songMatch.Value;
			}

			RunApp("C:\\Program Files\\ImageMagick-7.1.1-Q16-HDRI\\convert.exe", new AppRunnerSettings
			{
				Arguments = new[] { songImageFilename, "-channel", "RGB", "-negate", songImageFilename },
			});

			if (!IsRetina)
			{
				RunApp("C:\\Program Files\\ImageMagick-7.1.1-Q16-HDRI\\convert.exe", new AppRunnerSettings
				{
					Arguments = new[] { songImageFilename, "-filter", "Catrom", "-resize", "600%", songImageFilename },
				});
			}

			var songName = RunTesseractAndGetSongName();
			if (songName == null)
			{
				Console.WriteLine($"Unable to find song for {track}!");
				return;
			}

			var songTags = songTagsLookup[songName];

			// Customize these
			var maxSongWidth = 960;
			var outerPadding = 10;
			var gradientBlend = 250;
			var coverSize = 200;
			var innerPadding = 5;
			var wrapTextWidth = maxSongWidth - coverSize - gradientBlend - outerPadding * 2;

			using var cover = Image.Load(songTags.Tag.Pictures[0].Data.ToArray());
			cover.Mutate(x => x.Resize(coverSize, coverSize));

			var title = songTags.Tag.Title;
			var artists = songTags.Tag.Performers.Join(",");
			var isPlayingText = isTrackMaster ? "NOW PLAYING" : "PREVIOUS";

			var font = new Font(SystemFonts.CreateFont("Arial", 24.0f), 24.0f);
			var boldFont = new Font(SystemFonts.CreateFont("Arial", 30.0f, FontStyle.Bold), 30.0f);
			var fontColor = new Color(new Rgba32(255, 255, 255));

			var titleSize = TextMeasurer.Measure(title, new RendererOptions(boldFont) { WrappingWidth = wrapTextWidth });
			var artistsSize = TextMeasurer.Measure(artists, new RendererOptions(font) { WrappingWidth = wrapTextWidth });
			var isPlayingTextSize = TextMeasurer.Measure(isPlayingText, new RendererOptions(boldFont));

			var drawingOptions = new DrawingOptions
			{
				TextOptions = new TextOptions
				{
					WrapTextWidth = wrapTextWidth,
				},
			};

			using var songMetadata = new Image<Rgba32>(maxSongWidth, coverSize + outerPadding * 3 + innerPadding * 2 + (int)isPlayingTextSize.Height);
			var topLine = innerPadding * 2 + outerPadding * 2 + (int)isPlayingTextSize.Height;
			var coverWithPadding = coverSize + outerPadding * 2;
			var gradientWidth = coverWithPadding + titleSize.Width + gradientBlend;

			if (track == Track.Left)
			{
				var gradientBrush = new LinearGradientBrush(new PointF(0, 0), new PointF(gradientWidth, 0), GradientRepetitionMode.None, new ColorStop(0, new Rgba32(0, 0, 0, 0.5f)), new ColorStop((coverWithPadding + titleSize.Width) / gradientWidth, new Rgba32(0, 0, 0, 0.35f)), new ColorStop(0.9f, new Rgba32(0, 0, 0, 0)));
				songMetadata.Mutate(x => x
					.Fill(gradientBrush, new RectangleF(0, topLine - outerPadding, gradientWidth, coverWithPadding))
					.DrawImage(cover, new Point(outerPadding, topLine), 1.0f)
					.Fill(new SolidBrush(Color.Red), new RectangleF(outerPadding, 0, isPlayingTextSize.Width + outerPadding, isPlayingTextSize.Height + outerPadding))
					.DrawText(new DrawingOptions(), isPlayingText, boldFont, fontColor, new PointF(outerPadding + innerPadding, innerPadding))
					.DrawText(drawingOptions, title ?? "", boldFont, fontColor, new PointF(coverWithPadding, topLine))
					.DrawText(drawingOptions, artists ?? "", font, fontColor, new PointF(coverWithPadding, topLine + titleSize.Height))
					.DrawText(drawingOptions, songTags.Tag.Publisher ?? "", font, fontColor, new PointF(coverWithPadding, topLine + titleSize.Height + artistsSize.Height)));
			}
			else if (track == Track.Right)
			{
				drawingOptions.TextOptions.HorizontalAlignment = HorizontalAlignment.Right;

				var gradientBrush = new LinearGradientBrush(new PointF(maxSongWidth, 0), new PointF(maxSongWidth - gradientWidth, 0), GradientRepetitionMode.None, new ColorStop(0, new Rgba32(0, 0, 0, 0.5f)), new ColorStop((coverWithPadding + titleSize.Width) / gradientWidth, new Rgba32(0, 0, 0, 0.35f)), new ColorStop(0.9f, new Rgba32(0, 0, 0, 0)));
				songMetadata.Mutate(x => x
					.Fill(gradientBrush, new RectangleF(maxSongWidth - gradientWidth, topLine - outerPadding, gradientWidth, coverWithPadding))
					.DrawImage(cover, new Point(maxSongWidth - coverSize - outerPadding, topLine), 1.0f)
					.Fill(new SolidBrush(Color.Red), new RectangleF(maxSongWidth - outerPadding - innerPadding * 2 - isPlayingTextSize.Width, 0, isPlayingTextSize.Width + outerPadding, isPlayingTextSize.Height + outerPadding))
					.DrawText(new DrawingOptions(), isPlayingText, boldFont, fontColor, new PointF(maxSongWidth - outerPadding - innerPadding - isPlayingTextSize.Width, innerPadding))
					.DrawText(drawingOptions, title ?? "", boldFont, fontColor, new PointF(gradientBlend, topLine))
					.DrawText(drawingOptions, artists ?? "", font, fontColor, new PointF(gradientBlend, topLine + titleSize.Height))
					.DrawText(drawingOptions, songTags.Tag.Publisher ?? "", font, fontColor, new PointF(gradientBlend, topLine + titleSize.Height + artistsSize.Height)));
			}

			await songMetadata.SaveAsync($"{trackString}Metadata.png").ConfigureAwait(false);
		}

		private static bool TrackIsMaster(Image<Rgba32> bitmap)
		{
			for (int y = 0; y < bitmap.Height; y++)
			{
				for (int x = 0; x < bitmap.Width; x++)
				{
					var pixel = bitmap[x, y];
					if (pixel.R > 200)
						return true;
				}
			}

			return false;
		}

		private static Image<Rgba32> GetSongInfoBitmap(Image<Rgba32> rekordboxScreenshot, Track track)
		{
			var y = 190;
			var x = track switch
			{
				Track.Left => 1,
				Track.Right => rekordboxScreenshot.Width / 2 + 47,
				_ => throw new InvalidOperationException($"Invalid track kind: {track}!"),
			};
			var width = rekordboxScreenshot.Width / 2 - 63;
			var height = 44;

			var songInfoRect = new Rectangle(x, y, width, height);
			var songInfo = rekordboxScreenshot.Clone(x => x.Crop(songInfoRect));

			return songInfo;
		}

		private static Image<Rgba32> ScreenShot(IntPtr hWnd, string fileName)
		{
			GetWindowRect(hWnd, out var rc);
			var hWndDc = GetDC(hWnd);
			var hMemDc = CreateCompatibleDC(hWndDc);
			var hBitmap = CreateCompatibleBitmap(hWndDc, rc.Width, rc.Height);
			SelectObject(hMemDc, hBitmap);

			BitBlt(hMemDc, 0, 0, rc.Width, rc.Height, hWndDc, 0, 0, TernaryRasterOperations.SRCCOPY);
			var bitmap = System.Drawing.Bitmap.FromHbitmap(hBitmap);

			DeleteObject(hBitmap);
			ReleaseDC(hWnd, hWndDc);
			DeleteDC(hMemDc);

			using var memoryStream = new MemoryStream();
			bitmap.Save(memoryStream, System.Drawing.Imaging.ImageFormat.Jpeg);
			memoryStream.Seek(0, SeekOrigin.Begin);

			return Image.Load<Rgba32>(memoryStream, new JpegDecoder());
		}

		private enum TernaryRasterOperations : uint
		{
			/// <summary>dest = source</summary>
			SRCCOPY = 0x00CC0020,
			/// <summary>dest = source OR dest</summary>
			SRCPAINT = 0x00EE0086,
			/// <summary>dest = source AND dest</summary>
			SRCAND = 0x008800C6,
			/// <summary>dest = source XOR dest</summary>
			SRCINVERT = 0x00660046,
			/// <summary>dest = source AND (NOT dest)</summary>
			SRCERASE = 0x00440328,
			/// <summary>dest = (NOT source)</summary>
			NOTSRCCOPY = 0x00330008,
			/// <summary>dest = (NOT src) AND (NOT dest)</summary>
			NOTSRCERASE = 0x001100A6,
			/// <summary>dest = (source AND pattern)</summary>
			MERGECOPY = 0x00C000CA,
			/// <summary>dest = (NOT source) OR dest</summary>
			MERGEPAINT = 0x00BB0226,
			/// <summary>dest = pattern</summary>
			PATCOPY = 0x00F00021,
			/// <summary>dest = DPSnoo</summary>
			PATPAINT = 0x00FB0A09,
			/// <summary>dest = pattern XOR dest</summary>
			PATINVERT = 0x005A0049,
			/// <summary>dest = (NOT dest)</summary>
			DSTINVERT = 0x00550009,
			/// <summary>dest = BLACK</summary>
			BLACKNESS = 0x00000042,
			/// <summary>dest = WHITE</summary>
			WHITENESS = 0x00FF0062
		}

		[DllImport("gdi32.dll")]
		private static extern bool BitBlt(IntPtr hdc, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, TernaryRasterOperations dwRop);

		[DllImport("gdi32.dll", ExactSpelling = true, PreserveSig = true, SetLastError = true)]
		static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

		[DllImport("gdi32.dll")]
		private static extern bool DeleteObject(IntPtr hObject);

		[DllImport("gdi32.dll")]
		private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

		[DllImport("gdi32.dll", SetLastError = true)]
		private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

		[DllImport("user32.dll")]
		private static extern IntPtr GetDC(IntPtr hWnd);

		[DllImport("user32.dll")]
		private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

		[DllImport("gdi32.dll")]
		private static extern bool DeleteDC(IntPtr hDC);

		[DllImport("user32.dll")]
		private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

		public enum Track
		{
			Left,
			Right,
			None,
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct RECT
		{
			private int _Left;
			private int _Top;
			private int _Right;
			private int _Bottom;

			public RECT(RECT Rectangle) : this(Rectangle.Left, Rectangle.Top, Rectangle.Right, Rectangle.Bottom)
			{
			}
			public RECT(int Left, int Top, int Right, int Bottom)
			{
				_Left = Left;
				_Top = Top;
				_Right = Right;
				_Bottom = Bottom;
			}

			public int X
			{
				get { return _Left; }
				set { _Left = value; }
			}
			public int Y
			{
				get { return _Top; }
				set { _Top = value; }
			}
			public int Left
			{
				get { return _Left; }
				set { _Left = value; }
			}
			public int Top
			{
				get { return _Top; }
				set { _Top = value; }
			}
			public int Right
			{
				get { return _Right; }
				set { _Right = value; }
			}
			public int Bottom
			{
				get { return _Bottom; }
				set { _Bottom = value; }
			}
			public int Height
			{
				get { return _Bottom - _Top; }
				set { _Bottom = value + _Top; }
			}
			public int Width
			{
				get { return _Right - _Left; }
				set { _Right = value + _Left; }
			}
			public Point Location
			{
				get { return new Point(Left, Top); }
				set
				{
					_Left = value.X;
					_Top = value.Y;
				}
			}
			public Size Size
			{
				get { return new Size(Width, Height); }
				set
				{
					_Right = value.Width + _Left;
					_Bottom = value.Height + _Top;
				}
			}

			public static implicit operator Rectangle(RECT Rectangle)
			{
				return new Rectangle(Rectangle.Left, Rectangle.Top, Rectangle.Width, Rectangle.Height);
			}
			public static implicit operator RECT(Rectangle Rectangle)
			{
				return new RECT(Rectangle.Left, Rectangle.Top, Rectangle.Right, Rectangle.Bottom);
			}
			public static bool operator ==(RECT Rectangle1, RECT Rectangle2)
			{
				return Rectangle1.Equals(Rectangle2);
			}
			public static bool operator !=(RECT Rectangle1, RECT Rectangle2)
			{
				return !Rectangle1.Equals(Rectangle2);
			}

			public override string ToString()
			{
				return "{Left: " + _Left + "; " + "Top: " + _Top + "; Right: " + _Right + "; Bottom: " + _Bottom + "}";
			}

			public override int GetHashCode()
			{
				return ToString().GetHashCode();
			}

			public bool Equals(RECT Rectangle)
			{
				return Rectangle.Left == _Left && Rectangle.Top == _Top && Rectangle.Right == _Right && Rectangle.Bottom == _Bottom;
			}

			public override bool Equals(object Object)
			{
				if (Object is RECT)
				{
					return Equals((RECT)Object);
				}
				else if (Object is Rectangle)
				{
					return Equals(new RECT((Rectangle)Object));
				}

				return false;
			}
		}
	}
}
