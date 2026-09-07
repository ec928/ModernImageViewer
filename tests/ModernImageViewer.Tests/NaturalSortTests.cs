using Xunit;

namespace ModernImageViewer.Tests
{
    public class NaturalSortTests
    {
        [Fact]
        public void Numbered_files_sort_the_way_Explorer_shows_them()
        {
            // The order Explorer displays for this folder, per the reported screenshot.
            string[] expected =
            {
                "1.webp", "2.webp", "3.webp", "4.webp",
                "5.webp", "5a.jpg", "5b.jpg", "5c.jpg",
                "6.webp", "7.webp", "7a.jpg", "8.webp", "9.webp",
                "10.webp", "11.webp", "12.webp", "13.webp", "14.webp",
                "15.webp", "16.webp", "17.webp", "18.webp", "19.webp",
            };

            var shuffled = new[]
            {
                "13.webp", "5b.jpg", "1.webp", "19.webp", "7a.jpg", "10.webp",
                "3.webp", "5.webp", "16.webp", "2.webp", "9.webp", "5c.jpg",
                "11.webp", "4.webp", "18.webp", "6.webp", "14.webp", "5a.jpg",
                "8.webp", "12.webp", "17.webp", "7.webp", "15.webp",
            };

            System.Array.Sort(shuffled, NaturalSort.Compare);
            Assert.Equal(expected, shuffled);
        }

        [Fact]
        public void Ten_sorts_after_two_not_between_one_and_two()
        {
            // The reported bug: ordinal comparison gave 1, 10, 11, ... 2, 20.
            Assert.True(NaturalSort.Compare("2.webp", "10.webp") < 0);
            Assert.True(NaturalSort.Compare("10.webp", "9.webp") > 0);
        }

        [Fact]
        public void Leading_zeros_do_not_change_the_order()
        {
            Assert.True(NaturalSort.Compare("007.jpg", "8.jpg") < 0);
            Assert.True(NaturalSort.Compare("09.jpg", "10.jpg") < 0);
        }

        [Fact]
        public void Comparison_ignores_case_like_the_shell()
        {
            Assert.Equal(0, NaturalSort.Compare("IMG_1.JPG", "img_1.jpg"));
        }

        [Fact]
        public void Nulls_order_first_and_are_equal_to_each_other()
        {
            Assert.Equal(0, NaturalSort.Compare(null, null));
            Assert.True(NaturalSort.Compare(null, "1.jpg") < 0);
            Assert.True(NaturalSort.Compare("1.jpg", null) > 0);
        }

        [Fact]
        public void Comparer_instance_matches_the_static_method()
        {
            Assert.Equal(
                System.Math.Sign(NaturalSort.Compare("2.jpg", "10.jpg")),
                System.Math.Sign(NaturalSort.Comparer.Compare("2.jpg", "10.jpg")));
        }
    }
}
