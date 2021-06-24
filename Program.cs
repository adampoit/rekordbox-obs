using System.IO;
using System.Linq;
using Faithlife.Build;
using Faithlife.Utility;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using static Faithlife.Build.AppRunner;

namespace rekordbox_obs
{
	class Program
	{
		static void Main(string[] args)
		{
			string EDMDirectory = "/Users/adam.poit/Music/EDM";

			int windowID = 0;
			RunApp("bash", new AppRunnerSettings
			{
				HandleOutputLine = (line) => int.TryParse(line, out windowID),
				Arguments = new[] { "-c", "GetWindowID rekordbox 'rekordbox'" },
			});

			var currentTrack = Track.None;
			var previousTrack = Track.None;
			while (true)
			{
				RunApp("screencapture", new AppRunnerSettings
				{
					Arguments = new[] { "-l", windowID.ToString(), "-x", "rekordbox.png" },
				});

				using var rekordboxScreenshot = Image.Load<Rgba32>("rekordbox.png");
				int x = 0;
				int y = 0;
				int width = 0;

				bool found = false;
				for (y = 0; y < rekordboxScreenshot.Height - 5; y++)
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

				var midpointX = width / 2;

				found = false;
				var color = new Rgba32(50, 50, 50);
				for (y = 0; y < rekordboxScreenshot.Height - 5; y++)
				{
					for (x = 0; x < rekordboxScreenshot.Width - 5; x++)
					{
						var topLeftPixel = rekordboxScreenshot[x, y];
						var topRightPixel = rekordboxScreenshot[x, y + 5];
						var bottomLeftPixel = rekordboxScreenshot[x + 5, y];
						var bottomRightPixel = rekordboxScreenshot[x + 5, y + 5];
						if (topLeftPixel == color && topRightPixel == color && bottomLeftPixel == color && bottomRightPixel == color)
						{
							found = true;
							break;
						}
					}

					if (found)
						break;
				}

				var songInfoRect = new Rectangle(x, y, rekordboxScreenshot.Width - x - 70, 85);
				using var songInfo = rekordboxScreenshot.Clone(x => x.Crop(songInfoRect));
				songInfo.Save("songInfo.png");

				var leftMasterRect = new Rectangle(midpointX - 200, 50, 80, 30);
				using var leftMaster = songInfo.Clone(x => x.Crop(leftMasterRect));
				bool isLeftMaster = TrackIsMaster(leftMaster);

				var rightMasterRect = new Rectangle(songInfo.Width - 90, 50, 80, 30);
				using var rightMaster = songInfo.Clone(x => x.Crop(rightMasterRect));
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

					var leftSongRect = new Rectangle(115, 10, midpointX - 450, 40);
					ProcessTrack(EDMDirectory, Track.Left, isLeftMaster, songInfo, leftSongRect);

					var rightSongRect = new Rectangle(midpointX + 225, 10, midpointX - 450, 40);
					ProcessTrack(EDMDirectory, Track.Right, isRightMaster, songInfo, rightSongRect);
				}
			}
		}

		private static void ProcessTrack(string EDMDirectory, Track track, bool isTrackMaster, Image<Rgba32> songInfo, Rectangle songRect)
		{
			var trackString = $"{track.ToString().ToLower()}Song";

			using var song = songInfo.Clone(x => x.Crop(songRect));
			var songImageFilename = $"{trackString}.png";
			song.Save(songImageFilename);

			RunApp("tesseract", new AppRunnerSettings
			{
				Arguments = new[] { songImageFilename, trackString },
			});
			var songName = File.ReadAllLines($"{trackString}.txt").First();

			string songFilename = null;
			RunApp("fzf", new AppRunnerSettings
			{
				WorkingDirectory = EDMDirectory,
				Arguments = new[] { "--filter", songName.Replace("...", "") },
				HandleOutputLine = (line) => songFilename = line,
			});
			var songTags = TagLib.File.Create(System.IO.Path.Combine(EDMDirectory, songFilename));

			using var cover = Image.Load(songTags.Tag.Pictures[0].Data.ToArray());
			cover.Mutate(x => x.Resize(200, 200));

			var title = songTags.Tag.Title;
			var artists = songTags.Tag.Performers.Join(",");
			var isPlayingText = isTrackMaster ? "NOW PLAYING" : "PREVIOUS";
			var wrapTextWidth = 730;

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

			using var songMetadata = new Image<Rgba32>(960, 230 + (int)isPlayingTextSize.Height);
			var topLine = 20 + (int)isPlayingTextSize.Height;

			if (track == Track.Left)
			{
				songMetadata.Mutate(x => x
					.DrawImage(cover, new Point(10, topLine), 1.0f)
					.Fill(new SolidBrush(Color.Red), new RectangleF(10, 0, isPlayingTextSize.Width + 10, isPlayingTextSize.Height + 10))
					.DrawText(new DrawingOptions(), isPlayingText, boldFont, fontColor, new PointF(15, 5))
					.DrawText(drawingOptions, title, boldFont, fontColor, new PointF(220, topLine))
					.DrawText(drawingOptions, artists, font, fontColor, new PointF(220, topLine + titleSize.Height))
					.DrawText(drawingOptions, songTags.Tag.Publisher, font, fontColor, new PointF(220, topLine + titleSize.Height + artistsSize.Height)));
			}
			else if (track == Track.Right)
			{
				drawingOptions.TextOptions.HorizontalAlignment = HorizontalAlignment.Right;

				songMetadata.Mutate(x => x
					.DrawImage(cover, new Point(750, topLine), 1.0f)
					.Fill(new SolidBrush(Color.Red), new RectangleF(940 - isPlayingTextSize.Width, 0, isPlayingTextSize.Width + 10, isPlayingTextSize.Height + 10))
					.DrawText(new DrawingOptions(), isPlayingText, boldFont, fontColor, new PointF(945 - isPlayingTextSize.Width, 5))
					.DrawText(drawingOptions, title, boldFont, fontColor, new PointF(10, topLine))
					.DrawText(drawingOptions, artists, font, fontColor, new PointF(10, topLine + titleSize.Height))
					.DrawText(drawingOptions, songTags.Tag.Publisher, font, fontColor, new PointF(10, topLine + titleSize.Height + artistsSize.Height)));
			}

			songMetadata.Save($"{trackString}Metadata.png");
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

		public enum Track
		{
			Left,
			Right,
			None,
		}
	}
}
