using System;

namespace InspectionExample
{
    public interface ISink
    {
        void Write(int value);
    }

    public partial class ReviewCounter
    {
        private int value;

        public void Adjust(bool enabled)
        {
            int next = this.ReadNext();
            try
            {
                if (enabled)
                {
                    this.Commit(next);
                    this.Commit(next);
                }
            }
            finally
            {
                this.Release();
            }
        }

        private int ReadNext()
        {
            return checked(this.value + 1);
        }

        private void Commit(int next)
        {
            this.Observe(next);
            this.value = next;
        }

        private void Release()
        {
            this.value = 0;
        }

        public void Dispatch(ISink sink)
        {
            sink.Write(this.value);
            this.VirtualWrite(this.value);
            Console.WriteLine(this.value);
        }

        protected virtual void VirtualWrite(int item)
        {
            this.value = item;
        }

        public void Generic()
        {
            this.Observe(Echo(1).Identity());
        }

        private static T Echo<T>(T item)
        {
            return item;
        }

        partial void Observe(int item);

        public void Nested()
        {
            Action callback = () => this.Release();
            callback();
        }
    }

    public partial class ReviewCounter
    {
        partial void Observe(int item)
        {
            this.value = item;
        }
    }

    public static class ReviewExtensions
    {
        public static T Identity<T>(this T item)
        {
            return item;
        }
    }
}
