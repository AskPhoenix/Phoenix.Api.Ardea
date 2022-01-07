using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Repositories;
using Phoenix.DataHandle.WordPress;
using Phoenix.DataHandle.WordPress.Models;
using Phoenix.DataHandle.WordPress.Models.Uniques;
using Phoenix.DataHandle.WordPress.Wrappers;
using WordPressPCL.Models;

namespace Phoenix.Api.Ardea.Pullers
{
    public class CoursePuller : WPPuller
    {
        private readonly CourseRepository courseRepository;
        private readonly BookRepository bookRepository;

        private readonly Dictionary<int, string> schoolTimezonesDict;

        public CoursePuller(Dictionary<int, SchoolUnique> schoolUqsDict, 
            PhoenixContext phoenixContext, ILogger logger, SchoolUnique? specificSchoolUq = null, bool verbose = true) 
            : base(schoolUqsDict, logger, specificSchoolUq, verbose)
        {
            this.courseRepository = new(phoenixContext);
            this.bookRepository = new(phoenixContext);

            this.schoolTimezonesDict = FindSchoolTimezones(phoenixContext, schoolUqsDict.Keys);
        }

        public override int CategoryId => PostCategoryWrapper.GetCategoryId(PostCategory.Course);

        public override async Task<int[]> PullAsync()
        {
            Logger.LogInformation("-----------------------------------------");
            Logger.LogInformation("Courses & Books synchronization started");

            IEnumerable<Post> coursePosts = await WordPressClientWrapper.GetPostsAsync(CategoryId);
            IEnumerable<Post> filteredPosts;

            int P = coursePosts.Count();
            int[] updatedIds = new int[P];

            int p = 0;
            foreach (var schoolUqPair in SchoolUqsDict)
            {
                filteredPosts = coursePosts.FilterPostsForSchool(schoolUqPair.Value);
                
                Logger.LogInformation("{CoursesNumber} Courses found for School \"{SchoolUq}\"", 
                    filteredPosts.Count(), schoolUqPair.Value.ToString());

                foreach (var coursePost in filteredPosts)
                {
                    var courseAcf = (CourseACF)(await WordPressClientWrapper.GetAcfAsync<CourseACF>(coursePost.Id)).WithTitleCase();
                    courseAcf.SchoolUnique = schoolUqPair.Value;
                    courseAcf.SchoolTimeZone = schoolTimezonesDict[schoolUqPair.Key];

                    // TODO: Create property of type CourseUnique in CourseACF
                    var courseUq = new CourseUnique(courseAcf.SchoolUnique, courseAcf.Code);

                    var course = await courseRepository.Find(courseAcf.MatchesUnique);
                    if (course is null)
                    {
                        if (Verbose)
                            Logger.LogInformation("Adding Course: {CourseUq}", courseUq.ToString());

                        course = courseAcf.ToContext();
                        course.SchoolId = schoolUqPair.Key;

                        courseRepository.Create(course);
                    }
                    else
                    {
                        if (Verbose)
                            Logger.LogInformation("Updating Course: {CourseUq}", courseUq.ToString());
                        courseRepository.Update(course, courseAcf.ToContext());
                    }

                    updatedIds[p++] = course.Id;
                    CourseUqsDict.Add(course.Id, courseUq);

                    if (Verbose)
                        Logger.LogInformation("Synchronizing Books for Course: {CourseUq}", courseUq.ToString());

                    var books = courseAcf.ExtractBooks();
                    var bookIds = new int[books.Count()];

                    int b = 0;
                    foreach (var book in books)
                    {
                        Book ctxBook = await bookRepository.Find(b => b.NormalizedName == book.NormalizedName);
                        if (ctxBook is null)
                        {
                            if (Verbose)
                                Logger.LogInformation("Adding Book: {BookName}", book.Name);
                            bookRepository.Create(book);
                            ctxBook = book;
                        }
                        else
                        {
                            if (Verbose)
                                Logger.LogInformation("Updating Book: {BookName}", book.Name);
                            
                            ctxBook.Publisher = book.Publisher;
                            ctxBook.Info = book.Info;

                            bookRepository.Update(ctxBook);
                        }

                        bookIds[b++] = ctxBook.Id;
                    }

                    if (Verbose)
                        Logger.LogInformation("Linking Books with Course {CourseUq}", courseUq.ToString());
                    courseRepository.LinkBooks(course, bookIds, deleteAdditionalLinks: true);
                }
            }

            Logger.LogInformation("Courses & Books synchronization finished");
            Logger.LogInformation("------------------------------------------");

            return updatedIds;
        }

        public override Task<int[]> DeleteAsync(int[] toKeep)
        {
            // Books are never deleted

            throw new NotImplementedException();
        }
    }
}
