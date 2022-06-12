using Phoenix.DataHandle.DataEntry;
using Phoenix.DataHandle.DataEntry.Models.Uniques;
using Phoenix.DataHandle.Main.Entities;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Repositories;
using WordPressPCL.Models;

namespace Phoenix.Api.Ardea.Pullers
{
    public class CoursePuller : WPPuller<Course>
    {
        private readonly CourseRepository courseRepository;
        private readonly BookRepository bookRepository;
        private readonly SchoolRepository schoolRepository;

        private readonly Dictionary<int, string> schoolTimezonesDict;

        public List<int> PulledBookIds { get; } = new();

        public override PostCategory PostCategory => PostCategory.Course;

        public CoursePuller(Dictionary<int, SchoolUnique> schoolUqsDict,
            PhoenixContext phoenixContext, ILogger logger, bool verbose = true) 
            : base(schoolUqsDict, logger, verbose)
        {
            this.courseRepository = new(phoenixContext);
            this.bookRepository = new(phoenixContext);
            this.schoolRepository = new(phoenixContext);

            // TODO: Check where to use
            this.schoolTimezonesDict = Task.Run(() => FindSchoolTimezonesAsync(phoenixContext, schoolUqsDict.Keys)).Result;
        }

        public override async Task<List<int>> PullAsync()
        {
            Logger.LogInformation("-----------------------------------------------------------------");
            Logger.LogInformation("Courses & Books synchronization started");

            IEnumerable<Post> coursePosts = await WPClientWrapper.GetPostsAsync(this.PostCategory);
            IEnumerable<Post> filteredPosts;

            var toCreate = new List<Course>();
            var toUpdate = new List<Course>();
            var toUpdateFrom = new List<Course>();

            var booksToCreate = new List<Book>();
            var booksToUpdate = new List<Book>(); // Used only to store the book ids

            foreach (var schoolUqPair in SchoolUqsDict)
            {
                filteredPosts = coursePosts.FilterPostsForSchool(schoolUqPair.Value);
                
                Logger.LogInformation("{CoursesNumber} courses found for School \"{SchoolUq}\"", 
                    filteredPosts.Count(), schoolUqPair.Value.ToString());

                foreach (var coursePost in filteredPosts)
                {
                    var courseAcf = await WPClientWrapper.GetCourseAcfAsync(coursePost);
                    var courseUq = courseAcf.GetCourseUnique(schoolUqPair.Value);
                    var course = await courseRepository.FindUniqueAsync(courseUq);

                    if (course is null)
                    {
                        if (Verbose)
                            Logger.LogInformation("Course with code {CourseCode} to be created.", courseUq.Code);

                        course = (Course)(ICourse)courseAcf;
                        course.SchoolId = schoolUqPair.Key;

                        toCreate.Add(course);
                    }
                    else
                    {
                        if (Verbose)
                            Logger.LogInformation("Course with code {CourseCode} to be updated.", courseUq.Code);

                        toUpdate.Add(course);
                        toUpdateFrom.Add((Course)(ICourse)courseAcf);
                    }

                    CourseUqsDict.Add(course.Id, courseUq);

                    if (Verbose)
                        Logger.LogInformation("Synchronizing books for course with code {CourseCode}.", courseUq.Code);

                    var courseBooks = courseAcf.Books.Cast<Book>();

                    foreach (var book in courseBooks)
                    {
                        var ctxBook = await bookRepository.FindUniqueAsync(book.Name);
                        if (ctxBook is null)
                        {
                            if (Verbose)
                                Logger.LogInformation("Book \"{BookName}\" to be created.", book.Name);

                            booksToCreate.Add(book);
                        }
                        else
                        {
                            if (Verbose)
                                Logger.LogInformation("Book \"{BookName}\" already exists.", book.Name);

                            booksToUpdate.Add(ctxBook);

                            //ctxBook.Publisher = book.Publisher;
                            //ctxBook.Info = book.Info;
                            //bookRepository.Update(ctxBook);
                        }
                    }

                    if (Verbose)
                        Logger.LogInformation("Linking books with course with code {CourseCode}.", courseUq.Code);

                    // TODO: To check if books are linked and if old links are deleted
                    // TODO: Check if course objects affects the object in the list. If not, use that one
                    foreach (var book in courseBooks)
                        course.Books.Add(book);
                }
            }

            Logger.LogInformation("Creating {ToCreateNum} courses...", toCreate.Count);
            var created = await courseRepository.CreateRangeAsync(toCreate);
            Logger.LogInformation("{CreatedNum}/{ToCreateNum} courses created successfully.",
                created.Count(), toCreate.Count);

            Logger.LogInformation("Creating {ToCreateNum} books...", booksToCreate.Count);
            var booksCreated = await bookRepository.CreateRangeAsync(booksToCreate);
            Logger.LogInformation("{CreatedNum}/{ToCreateNum} books created successfully.",
                booksCreated.Count(), booksToCreate.Count);

            Logger.LogInformation("Updating {ToUpdateNum} courses...", toUpdate.Count);
            var updated = await courseRepository.UpdateRangeAsync(toUpdate, toUpdateFrom);
            var restored = await courseRepository.RestoreRangeAsync(toUpdate);
            Logger.LogInformation("{UpdatedNum}/{ToUpdateNum} courses updated successfully.",
                updated.Count() + restored.Count(), toUpdate.Count);

            Logger.LogInformation("Courses & Books synchronization finished");
            Logger.LogInformation("-----------------------------------------------------------------");

            foreach (var course in created.Concat(updated))
                CourseUqsDict.Add(course.Id, new CourseUnique(new SchoolUnique(course.School.Code), course.Code));

            PulledBookIds.AddRange(booksCreated.Concat(booksToUpdate).Select(b => b.Id).Distinct().ToList());
            PulledIds.AddRange(CourseUqsDict.Keys);
            
            return PulledIds;
        }

        public override async Task<List<int>> ObviateAsync(List<int> toKeep)
        {
            // Books are never obviated

            IEnumerable<Course> findCoursesForSchool(int schoolId)
            {
                var school = Task.Run(() => schoolRepository.FindPrimaryAsync(schoolId)).Result;
                if (school is null)
                    return Enumerable.Empty<Course>();

                return school.Courses;
            }

            return ObviatedIds = await ObviateAllPerSchoolAsync(findCoursesForSchool, courseRepository, toKeep);
        }
    }
}
