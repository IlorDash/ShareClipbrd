using ShareClipbrd.Core.Clipboard;
using ShareClipbrd.Core.Helpers;

namespace ShareClipbrd.Core.Tests.Clipboard {
    public class ImageConverterTests {


        [Test]
        public void FromDibToBmpFileData_Test() {
            var streamDib = new MemoryStream(TestData.Dib_32x32);
            var data = ImageConverter.FromDibToBmpFileData(streamDib);
            Assert.That(data, Has.Length.GreaterThan(StructHelper.Size<BitmapFile.BITMAPFILEHEADER>()));
            var bmpHeader = StructHelper.FromBytes<BitmapFile.BITMAPFILEHEADER>(data);
            Assert.That(bmpHeader.bfType, Is.EqualTo(0x4D42));
            Assert.That(bmpHeader.bfSize, Is.LessThan(data.Length).And.GreaterThan(StructHelper.Size<BitmapFile.BITMAPFILEHEADER>()));
        }

        [Test]
        public void FromDibToBmpFileData_For_Incorrect_Data_Length_Throws_ArgumentException() {
            var streamDib = new MemoryStream(TestData.Dib_32x32.Skip(1).ToArray());
            var ex = Assert.Throws<ArgumentException>(() => ImageConverter.FromDibToBmpFileData(streamDib));
            Assert.That(ex.Message, Is.EqualTo("Deserialize BITMAPINFO. data invalid"));
        }

        [Test]
        public void FromDibToBmpFileData_For_Incorrect_Header_Throws_ArgumentException() {
            var data = TestData.Dib_32x32.ToArray();
            data[0]--;
            var streamDib = new MemoryStream(data);
            var ex = Assert.Throws<ArgumentException>(() => ImageConverter.FromDibToBmpFileData(streamDib));
            Assert.That(ex.Message, Is.EqualTo("Deserialize BITMAPINFO. data invalid"));
        }
    }
}
