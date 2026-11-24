-- no foreign keys on purpose: many student scripts do not declare them
CREATE DATABASE LibraryDB;
USE LibraryDB;
CREATE TABLE Author (AuthorID INT PRIMARY KEY, AuthorName VARCHAR(50), Country VARCHAR(30));
CREATE TABLE Book (BookID INT PRIMARY KEY, Title VARCHAR(80), Genre VARCHAR(20), Price DECIMAL(8,2), PublishedYear INT, AuthorID INT);
CREATE TABLE Member (MemberID INT PRIMARY KEY, Name VARCHAR(50), MemberType VARCHAR(20), City VARCHAR(30));
CREATE TABLE Loan (LoanID INT PRIMARY KEY, BookID INT, MemberID INT, LoanDate DATE, ReturnDate DATE);
INSERT INTO Author VALUES (1,'Humayun Ahmed','Bangladesh'),(2,'George Orwell','UK'),(3,'Haruki Murakami','Japan'),(4,'Zadie Smith','UK');
INSERT INTO Book VALUES (1,'Deyal','Fiction',450,2013,1),(2,'1984','Fiction',350,1949,2),(3,'Animal Farm','Satire',280,1945,2),(4,'Norwegian Wood','Fiction',620,1987,3),(5,'Himu','Humor',300,1990,1),(6,'Essays','Nonfiction',500,1946,2),(7,'Underground','Nonfiction',550,1997,3);
INSERT INTO Member VALUES (1,'Arif','Student','Dhaka'),(2,'Bina','Teacher','Dhaka'),(3,'Chandan','Student','Khulna'),(4,'Dipa','Guest','Sylhet');
INSERT INTO Loan VALUES (1,1,1,'2025-01-02','2025-01-20'),(2,2,1,'2025-02-01',NULL),(3,4,2,'2025-02-11','2025-03-01'),(4,5,3,'2025-03-03',NULL),(5,6,2,'2025-03-10',NULL);
