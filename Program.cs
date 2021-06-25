using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Faithlife.Build;
using Faithlife.Utility;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
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
			string EDMDirectory = "/Users/adam.poit/Music/EDM";

			var possibleWindowIds = new List<int>();
			RunApp("bash", new AppRunnerSettings
			{
				HandleOutputLine = (line) =>
				{
					var match = Regex.Match(line, "\"rekordbox\".*id=(\\d+)");
					if (match.Success)
						possibleWindowIds.Add(int.Parse(match.Groups[1].Value));
				},
				Arguments = new[] { "-c", "GetWindowID rekordbox --list" },
			});

			int windowId = 0;
			// int midpointX = 0;
			foreach (var possibleWindowId in possibleWindowIds)
			{
				RunApp("screencapture", new AppRunnerSettings
				{
					Arguments = new[] { "-l", possibleWindowId.ToString(), "-x", "rekordbox.png" },
				});

				using var rekordboxScreenshot = Image.Load<Rgba32>("rekordbox.png");
				var width = GetWindowWidth(rekordboxScreenshot);
				if (width != 0)
				{
					windowId = possibleWindowId;
					// midpointX = width / 2;
				}
			}
			if (windowId == 0)
				throw new InvalidOperationException("Unable to locate rekordbox window.");

			var cancellationTokenSource = new CancellationTokenSource();
			Console.CancelKeyPress += (s, e) =>
			{
				e.Cancel = true;
				cancellationTokenSource.Cancel();
			};

			var currentTrack = Track.None;
			var previousTrack = Track.None;
			while (true)
			{
				try
				{
					if (cancellationTokenSource.IsCancellationRequested)
						break;

					RunApp("screencapture", new AppRunnerSettings
					{
						Arguments = new[] { "-l", windowId.ToString(), "-x", "rekordbox.png" },
					});

					using var rekordboxScreenshot = Image.Load<Rgba32>("rekordbox.png");

					using var leftSongInfo = GetSongInfoBitmap(rekordboxScreenshot, 0);
					var leftMasterRect = GetNearestRectangle(leftSongInfo.Width * 0.93, leftSongInfo.Height * 0.55, leftSongInfo.Width * 0.07, leftSongInfo.Height * 0.45);
					using var leftMaster = leftSongInfo.Clone(x => x.Crop(leftMasterRect));
					bool isLeftMaster = TrackIsMaster(leftMaster);

					using var rightSongInfo = GetSongInfoBitmap(rekordboxScreenshot, rekordboxScreenshot.Width / 2);
					var rightMasterRect = GetNearestRectangle(rightSongInfo.Width * 0.93, rightSongInfo.Height * 0.55, rightSongInfo.Width * 0.07, rightSongInfo.Height * 0.45);
					using var rightMaster = rightSongInfo.Clone(x => x.Crop(rightMasterRect));
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

						await ProcessTrack(EDMDirectory, Track.Left, isLeftMaster, leftSongInfo).ConfigureAwait(false);
						await ProcessTrack(EDMDirectory, Track.Right, isRightMaster, rightSongInfo).ConfigureAwait(false);
					}
				}
				catch (Exception)
				{
					if (!cancellationTokenSource.IsCancellationRequested)
						throw;
				}
			}

			foreach (var file in FindFiles("*.png*", "*.txt"))
				File.Delete(file);
		}

		private static int GetNearestInt(double value)
		{
			return (int)Math.Floor(value);
		}

		private static Rectangle GetNearestRectangle(double x, double y, double width, double height)
		{
			return new Rectangle(GetNearestInt(x), GetNearestInt(y), GetNearestInt(width), GetNearestInt(height));
		}

		private static async Task ProcessTrack(string EDMDirectory, Track track, bool isTrackMaster, Image<Rgba32> songInfo)
		{
			var trackString = $"{track.ToString().ToLower()}Song";

			var songRect = GetNearestRectangle(songInfo.Width * 0.08, songInfo.Height * 0.1, songInfo.Width - songInfo.Width * 0.24, songInfo.Height * 0.5);
			using var song = songInfo.Clone(x => x.Crop(songRect));
			var songImageFilename = $"{trackString}.png";
			await song.SaveAsync(songImageFilename).ConfigureAwait(false);

			string songFilename = null;
			void RunTesseractAndFzf()
			{
				RunApp("tesseract", new AppRunnerSettings
				{
					Arguments = new[] { songImageFilename, trackString },
				});
				var songName = File.ReadAllLines($"{trackString}.txt").First();
				if (songName == "Not Loaded.")
					return;

				RunApp("fzf", new AppRunnerSettings
				{
					WorkingDirectory = EDMDirectory,
					Arguments = new[] { "--filter", songName.Replace("...", "") },
					HandleOutputLine = (line) => songFilename = line,
				});
			}

			try
			{
				RunTesseractAndFzf();
			}
			catch (BuildException)
			{
				RunApp("convert", new AppRunnerSettings
				{
					Arguments = new[] { songImageFilename, "-filter", "Catrom", "-resize", "600%", songImageFilename },
				});

				RunTesseractAndFzf();
			}

			if (songFilename == null)
				return;

			var songTags = TagLib.File.Create(System.IO.Path.Combine(EDMDirectory, songFilename));

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
					.DrawText(drawingOptions, title, boldFont, fontColor, new PointF(coverWithPadding, topLine))
					.DrawText(drawingOptions, artists, font, fontColor, new PointF(coverWithPadding, topLine + titleSize.Height))
					.DrawText(drawingOptions, songTags.Tag.Publisher, font, fontColor, new PointF(coverWithPadding, topLine + titleSize.Height + artistsSize.Height)));
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
					.DrawText(drawingOptions, title, boldFont, fontColor, new PointF(gradientBlend, topLine))
					.DrawText(drawingOptions, artists, font, fontColor, new PointF(gradientBlend, topLine + titleSize.Height))
					.DrawText(drawingOptions, songTags.Tag.Publisher, font, fontColor, new PointF(gradientBlend, topLine + titleSize.Height + artistsSize.Height)));
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

		private static int GetWindowWidth(Image<Rgba32> rekordboxScreenshot)
		{
			int x = 0;
			int y = 0;
			int width = 0;

			bool found = false;
			for (y = 0; y < rekordboxScreenshot.Height; y++)
			{
				var pixelSpan = rekordboxScreenshot.GetPixelRowSpan(y);
				for (x = 0; x < rekordboxScreenshot.Width; x++)
				{
					var pixel = pixelSpan[x];
					if (pixel.R == 25 && pixel.G == 25 && pixel.B == 25)
					{
						found = true;
						width++;
					}
				}

				if (found)
					break;
			}

			return width;
		}

		private static Image<Rgba32> GetSongInfoBitmap(Image<Rgba32> rekordboxScreenshot, int startX)
		{
			int x = startX;
			int y = 0;
			int width = 0;
			int height = 0;
			bool found = false;
			var color = new Rgba32(50, 50, 50);
			var black = new Rgba32(0, 0, 0);

			for (y = 0; y < rekordboxScreenshot.Height; y++)
			{
				for (x = startX; x < rekordboxScreenshot.Width; x++)
				{
					var pixel = rekordboxScreenshot[x, y];
					if (pixel == color)
					{
						width = 0;
						while (rekordboxScreenshot[x + width + 1, y] == color)
							width++;

						if (width < rekordboxScreenshot.Width / 4)
							continue;

						height = 1;
						while (rekordboxScreenshot[x + width - 2, y + height + 1] == black)
							height++;

						found = true;
						break;
					}
				}

				if (found)
					break;
			}

			var songInfoRect = new Rectangle(x, y, width, height);
			var songInfo = rekordboxScreenshot.Clone(x => x.Crop(songInfoRect));

			return songInfo;
		}

		public enum Track
		{
			Left,
			Right,
			None,
		}
	}
}
