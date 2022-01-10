using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Repositories;
using Phoenix.DataHandle.WordPress;
using Phoenix.DataHandle.WordPress.Models;
using Phoenix.DataHandle.WordPress.Models.Uniques;
using Phoenix.DataHandle.WordPress.Wrappers;
using WordPressPCL.Models;

namespace Phoenix.Api.Ardea.Pullers
{
    public class CoursePuller : WPPuller<Course>
    {
        private readonly CourseRepository courseRepository;
        private readonly BookRepository bookRepository;
        private readonly SchoolRepository schoolRepository;

        private readonly Dictionary<int, string> schoolTimezonesDict;

        public List<int> PulledBookIds { get; private set; }

        public CoursePuller(Dictionary<int, SchoolUnique> schoolUqsDict, 
            PhoenixContext phoenixContext, ILogger logger, bool verbose = true) 
            : base(schoolUqsDict, logger, verbose)
        {
            this.courseRepository = new(phoenixContext);
            this.bookRepository = new(phoenixContext);
            this.schoolRepository = new(phoenixContext);

            this.schoolTimezonesDict = FindSchoolTimezones(phoenixContext, schoolUqsDict.Keys);

            this.PulledBookIds = new List<int>();
        }

        public override int CategoryId => PostCategoryWrapper.GetCategoryId(PostCategory.Course);

        public override async Task<List<int>> PullAsync()
        {
            Logger.LogInformation("-----------------------------------------------------------------");
            Logger.LogInformation("Courses & Books synchronization started");

            IEnumerable<Post> coursePosts = await WordPressClientWrapper.GetPostsAsync(CategoryId);
            IEnumerable<Post> filteredPosts;

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                filteredPosts = coursePosts.FilterPostsForSchool(schoolUqPair.Value);
                
                Logger.LogInformation("{CoursesNumber} courses found for School \"{SchoolUq}\"", 
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
                            Logger.LogInformation("Adding course with code {CourseCode}", courseUq.Code);

                        course = courseAcf.ToContext();
                        course.SchoolId = schoolUqPair.Key;

                        courseRepository.Create(course);
                    }
                    else
                    {
                        if (Verbose)
                            Logger.LogInformation("Updating course with code {CourseCode}", courseUq.Code);
                        courseRepository.Update(course, courseAcf.ToContext());
                        courseRepository.Restore(course);
                    }

                    CourseUqsDict.Add(course.Id, courseUq);

                    if (Verbose)
                        Logger.LogInformation("Synchronizing books for course with code {CourseCode}", courseUq.Code);

                    var booksToLink = courseAcf.ExtractBooks();
                    var bookIdsToLink = new int[booksToLink.Count()];

                    int b = 0;
                    foreach (var book in booksToLink)
                    {
                        Book ctxBook = await bookRepository.Find(b => b.NormalizedName == book.NormalizedName);
                        if (ctxBook is null)
                        {
                            if (Verbose)
                                Logger.LogInformation("Adding book: {BookName}", book.Name);
                            bookRepository.Create(book);
                            ctxBook = book;
                        }
                        else
                        {
                            if (Verbose)
                                Logger.LogInformation("Book \"{BookName}\" already exists", book.Name);
                            
                            //ctxBook.Publisher = book.Publisher;
                            //ctxBook.Info = book.Info;
                            //bookRepository.Update(ctxBook);
                        }

                        bookIdsToLink[b++] = ctxBook.Id;
                        PulledBookIds.Add(ctxBook.Id);
                    }

                    if (Verbose)
                        Logger.LogInformation("Linking books with course with code {CourseCode}", courseUq.Code);
                    courseRepository.LinkBooks(course, bookIdsToLink, deleteAdditionalLinks: true);
                }
            }

            Logger.LogInformation("Courses & Books synchronization finished");
            Logger.LogInformation("-----------------------------------------------------------------");

            PulledBookIds = PulledBookIds.Distinct().ToList();
            return PulledIds = CourseUqsDict.Keys.ToList();
        }

        public override async Task<List<int>> ObviateAsync(List<int> toKeep)
        {
            // Books are never obviated

            return ObviatedIds = await ObviateAllPerSchoolAsync(schoolRepository.FindCourses, courseRepository, toKeep);
        }
    }
}
